﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace rec Microsoft.VisualStudio.FSharp.Editor

open System
open System.Windows.Controls
open System.Windows.Media
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Formatting
open System.ComponentModel.Composition
open Microsoft.VisualStudio.Utilities
open Microsoft.CodeAnalysis
open System.Threading
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio
open Microsoft.VisualStudio.LanguageServices
open System.Windows
open System.Collections.Generic
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.CodeAnalysis.Editor.Shared.Extensions
open Microsoft.CodeAnalysis.Editor.Shared.Utilities
open Microsoft.CodeAnalysis.Classification
open Internal.Utilities.StructuredFormat
open Microsoft.VisualStudio.Text.Tagging
open System.Collections.Concurrent
open System.Collections
open System.Windows.Media.Animation
open System.Globalization

open Microsoft.VisualStudio.FSharp.Editor.Logging
open Microsoft.CodeAnalysis.Text

type CodeLensTag(width, topSpace, baseline, textHeight, bottomSpace, affinity, tag:obj, providerTag:obj) =
    inherit SpaceNegotiatingAdornmentTag(width, topSpace, baseline, textHeight, bottomSpace, affinity, tag, providerTag)
    
type internal CodeLens =
    { TaggedText: Async<(ResizeArray<Layout.TaggedText> * QuickInfoNavigation) option>
      mutable Computed: bool 
      mutable FullTypeSignature: string
      mutable UiElement: UIElement }

type internal CodeLensTagger  
    (
        workspace: Workspace, 
        documentId: Lazy<DocumentId>,
        buffer: ITextBuffer, 
        checker: FSharpChecker,
        projectInfoManager: ProjectInfoManager,
        typeMap: Lazy<ClassificationTypeMap>,
        gotoDefinitionService: FSharpGoToDefinitionService
     ) as self =
    inherit SimpleTagger<CodeLensTag>(buffer)

    static let candidate = "abcdefghijklmnopqrstuvwxyz->ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    
    static let MeasureString candidate (textBox:TextBlock)=
        let formattedText = 
            FormattedText(candidate, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Typeface(textBox.FontFamily, textBox.FontStyle, textBox.FontWeight, textBox.FontStretch),
                textBox.FontSize, Brushes.Black)
        Size(formattedText.Width, formattedText.Height)

    static let MeasureTextBlock textBlock =
        MeasureString candidate textBlock
    

    static let GetTextBlockSize = MeasureTextBlock (TextBlock())

    let visit pos parseTree = 
        AstTraversal.Traverse(pos, parseTree, { new AstTraversal.AstVisitorBase<_>() with 
            member this.VisitExpr(_path, traverseSynExpr, defaultTraverse, expr) =
                defaultTraverse(expr)
            
            override this.VisitInheritSynMemberDefn (_, _, _, _, range) = Some range

            override this.VisitTypeAbbrev( _, range) = Some range

            override this.VisitLetOrUse(binding, range) = Some range

            override this.VisitComponentInfo componentInfo = Some componentInfo.Range
        })

    let formatMap = lazy typeMap.Value.ClassificationFormatMapService.GetClassificationFormatMap "tooltip"
    //let visibleAdornments = ConcurrentDictionary()

    let mutable lastResults = Dictionary<string, TrackingTagSpan<CodeLensTag>>()
    let mutable firstTimeChecked = false
    let mutable bufferChangedCts = new CancellationTokenSource()
    let mutable layoutChangedCts = new CancellationTokenSource()
    let mutable view: IWpfTextView option = None
    let mutable codeLensLayer: IAdornmentLayer option = None
    let mutable recentFirstVsblLineNmbr, recentLastVsblLineNmbr = 0, 0
    let mutable updated = false
    let mutable addedAdornmentTags = Dictionary()

    let FSharpRangeToSpan (bufferSnapshot:ITextSnapshot) (range:range) =
        try
            let startLine, endLine = 
                bufferSnapshot.GetLineFromLineNumber(range.StartLine - 1),
                bufferSnapshot.GetLineFromLineNumber(range.EndLine - 1)
            let startPosition = startLine.Start.Add range.StartColumn
            let endPosition = endLine.Start.Add range.EndColumn
            Span(startPosition.Position, endPosition.Position - startPosition.Position) |> Some
        with e -> 
            logErrorf "Error: %A" e
            None

    let layoutTagToFormatting (layoutTag: LayoutTag) =
        layoutTag
        |> RoslynHelpers.roslynTag
        |> ClassificationTags.GetClassificationTypeName
        |> typeMap.Value.GetClassificationType
        |> formatMap.Value.GetTextProperties   

    let executeCodeLenseAsync () =  
        let uiContext = SynchronizationContext.Current
        updated <- true
        asyncMaybe {
            try
                let! view = view
                do! Async.Sleep 800 |> liftAsync
                logInfof "Rechecking code due to buffer edit!"
                let! document = workspace.CurrentSolution.GetDocument(documentId.Value) |> Option.ofObj
                let! options = projectInfoManager.TryGetOptionsForEditingDocumentOrProject(document)
                let! _, parsedInput, checkFileResults = checker.ParseAndCheckDocument(document, options, true, "CodeLens")
                logInfof "Getting uses of all symbols!"
                let! symbolUses = checkFileResults.GetAllUsesOfAllSymbolsInFile() |> liftAsync
                let textSnapshot = view.TextSnapshot.TextBuffer.CurrentSnapshot
                logInfof "Updating due to buffer edit!"

                // Clear existing data and cache flags
                // For adornments which are invalid due to signature changes (the tag is still valid though)
                let outdatedAdornments = Generic.List()
                // The results which are left.
                let oldResults = Dictionary(lastResults)

                let newResults = Dictionary()
                // Symbols which cache wasn't found yet
                let unattachedSymbols = Generic.List()
                // Tags which are new or need to be updated due to changes.
                let tagsToUpdate = Dictionary()
                let continuedAdornmentTags = Dictionary()

                let useResults (displayContext: FSharpDisplayContext, func: FSharpMemberOrFunctionOrValue) =
                    async {
                        try
                            let textSnapshot = view.TextSnapshot.TextBuffer.CurrentSnapshot
                            let lineNumber = Line.toZ func.DeclarationLocation.StartLine
                            //logInfof "Computing cache for line %A with content %A" lineNumber lineStr
                            if (lineNumber >= 0 || lineNumber < textSnapshot.LineCount) && 
                                not func.IsPropertyGetterMethod && 
                                not func.IsPropertySetterMethod then
                
                                match func.FullTypeSafe with
                                | Some ty ->
                                    let! displayEnv = checkFileResults.GetDisplayEnvForPos(func.DeclarationLocation.Start)
                            
                                    let displayContext =
                                        match displayEnv with
                                        | Some denv -> FSharpDisplayContext(fun _ -> denv)
                                        | None -> displayContext
                                 
                                    let typeLayout = ty.FormatLayout(displayContext)
                                    let taggedText = ResizeArray()
                                    Layout.renderL (Layout.taggedTextListR taggedText.Add) typeLayout |> ignore
                                    let navigation = QuickInfoNavigation(gotoDefinitionService, document, func.DeclarationLocation)
                                    // Because the data is available notify that this line should be updated, displaying the results
                                    return Some (taggedText, navigation)
                                | None -> 
                                    logWarningf "Couldn't acquire CodeLens data for function %A" func
                                    return None
                            else return None
                        with e -> 
                            logErrorf "Error in lazy code lens computation. %A" e
                            return None
                    }

                for symbolUse in symbolUses do
                    if symbolUse.IsFromDefinition then
                        match symbolUse.Symbol with
                        | :? FSharpEntity as entity ->
                            for func in entity.MembersFunctionsAndValues do
                                // Regardles of whether we are in a async maybe, we don't want to abort the whole process due to a single empty option.
                                let rawElement = visit func.DeclarationLocation.Start parsedInput
                                match rawElement with
                                | None -> ()
                                | Some declarationRange ->
                                    let! declarationSpan = FSharpRangeToSpan textSnapshot declarationRange
                                    let funcID = func.FullName
                                    let fullDeclarationText = (textSnapshot.GetText declarationSpan).Replace(func.CompiledName, funcID)
                                    let fullTypeSignature = func.FullType.ToString()
                                    // Try to re-use the last results
                                    if lastResults.ContainsKey(fullDeclarationText) then
                                        // Make sure that the results are usable
                                        let lastResult = lastResults.[fullDeclarationText]
                                        let codeLens = lastResult.Tag.IdentityTag :?> CodeLens
                                        if codeLens.FullTypeSignature = fullTypeSignature then
                                            // The results can be reused because the signature is the same
                                            if codeLens.Computed then
                                                // Just re-use the old results, changed nothing
                                                newResults.[fullDeclarationText] <- lastResult
                                                continuedAdornmentTags.[lastResult.Tag.IdentityTag] <- ()
                                                logInfof "Declaration %A can be reused. IdentityTag %A" fullDeclarationText lastResult.Tag.IdentityTag
                                                oldResults.Remove(fullDeclarationText) |> ignore // Just tracking this
                                            else
                                                // The old results aren't computed at all, because the line might have changed create new results
                                                tagsToUpdate.[lastResult] <- (fullDeclarationText,
                                                    { TaggedText = Async.cache (useResults (symbolUse.DisplayContext, func))
                                                      Computed = false
                                                      FullTypeSignature = fullTypeSignature
                                                      UiElement = null })
                                                oldResults.Remove(fullDeclarationText) |> ignore
                                        else
                                            // The signature is invalid so save the invalid data to remove it later (if those is valid)
                                            if codeLens.Computed && not(isNull(codeLens.UiElement))then
                                                // Track the old element for removal
                                                outdatedAdornments.Add codeLens.UiElement
                                                // Push back the new results
                                                tagsToUpdate.[lastResult] <- (fullDeclarationText,
                                                        { TaggedText = Async.cache (useResults (symbolUse.DisplayContext, func))
                                                          Computed = false
                                                          FullTypeSignature = fullTypeSignature
                                                          UiElement = null })
                                                oldResults.Remove(fullDeclarationText) |> ignore
                                    else
                                        // The symbol might be completely new or has slightly changed. 
                                        // We need to track this and iterate over the left entries to ensure that there isn't anything
                                        unattachedSymbols.Add((symbolUse, func, fullDeclarationText, fullTypeSignature))
                        | _ -> ()

                let runMaybeAsyncOnCtx ctx  f = asyncMaybe {
                  let currentCtx = SynchronizationContext.Current
                  do! Async.SwitchToContext ctx |> liftAsync
                  let result = f()
                  do! Async.SwitchToContext currentCtx |> liftAsync
                  return result
                }

                let addTag ts t = runMaybeAsyncOnCtx uiContext (fun () -> self.CreateTagSpan(ts, t))
                let removeTag k = runMaybeAsyncOnCtx uiContext (fun () -> self.RemoveTagSpan(k) |> ignore)
            
                // In best case this works quite fine because often enough we change only a small part of the file and not the complete.
                for unattachedSymbol in unattachedSymbols do
                    let symbolUse, func, fullDeclarationText, fullTypeSignature = unattachedSymbol
                    let test (v:KeyValuePair<_, TrackingTagSpan<CodeLensTag>>) =
                        let codeLens = v.Value.Tag.IdentityTag :?> CodeLens
                        codeLens.FullTypeSignature = fullTypeSignature
                    match oldResults |> Seq.tryFind test with
                    | Some res ->
                        let codeLens = res.Value.Tag.IdentityTag :?> CodeLens
                        if codeLens.Computed && (isNull codeLens.UiElement |> not) then
                            newResults.[fullDeclarationText] <- res.Value
                            tagsToUpdate.[res.Value] <- (fullDeclarationText, codeLens)
                            continuedAdornmentTags.[codeLens] <- ()
                        else
                            // The tag might be still valid but it hasn't been computed yet so create fresh results
                            tagsToUpdate.[res.Value] <- (fullDeclarationText,
                                { TaggedText = Async.cache (useResults (symbolUse.DisplayContext, func))
                                  Computed = false
                                  FullTypeSignature = fullTypeSignature
                                  UiElement = null })
                        let key = res.Key
                        oldResults.Remove(key) |> ignore // no need to check this entry again
                    | None ->
                        // This function hasn't got any cache and so it's completely new.
                        // So create completely new results
                        // And finally add a tag for this.
                        let res = 
                                { TaggedText = Async.cache (useResults (symbolUse.DisplayContext, func))
                                  Computed = false
                                  FullTypeSignature = fullTypeSignature
                                  UiElement = null }
                        try
                            let declarationSpan = textSnapshot.GetLineFromLineNumber(func.DeclarationLocation.StartLine - 1).Extent.Span
                            let tag, trackingSpan = 
                                CodeLensTag(0., GetTextBlockSize.Height, 0., 0., 0., PositionAffinity.Predecessor, res, self),
                                textSnapshot.CreateTrackingSpan(declarationSpan, SpanTrackingMode.EdgeInclusive)
                            let! newTag = addTag trackingSpan tag
                            newResults.[fullDeclarationText] <- newTag
                        with e -> logExceptionWithContext (e, "Code Lens tracking tag span creation")
                    ()

                for tagToUpdate in tagsToUpdate do
                    do! removeTag(tagToUpdate.Key)
                    let fullDeclarationText, tag = tagToUpdate.Value
                    let! newTag = addTag (tagToUpdate.Key.Span) (CodeLensTag(0., GetTextBlockSize.Height, 0., 0., 0., PositionAffinity.Predecessor, tag, self))
                    newResults.[fullDeclarationText] <- newTag

                lastResults <- newResults
                addedAdornmentTags <- continuedAdornmentTags
                do! Async.SwitchToContext uiContext |> liftAsync
                let! layer = codeLensLayer
                // Remove outdated and invalid results
                for oldResult in oldResults do
                    let codeLens = oldResult.Value.Tag.IdentityTag :?> CodeLens
                    match isNull codeLens.UiElement with
                    | false -> layer.RemoveAdornment codeLens.UiElement
                    | _ -> ()
                    do! removeTag oldResult.Value
                // Remove outdated adornments
                outdatedAdornments |> Seq.iter (fun e -> layer.RemoveAdornment e)
            
                updated <- true
                logInfof "Finished updating code lens."
            
                if not firstTimeChecked then
                    firstTimeChecked <- true
            with e -> logErrorf "%A" e
        } |> Async.Ignore
    
    do async {
          let mutable numberOfFails = 0
          while not firstTimeChecked && numberOfFails < 10 do
              try
                  do! executeCodeLenseAsync()
                  do! Async.Sleep(1000)
              with
              | e -> logErrorf "Code Lens startup failed with: %A" e
                     numberOfFails <- numberOfFails + 1
       } |> Async.Start

    let layoutUIElementOnLine (view:IWpfTextView) (line:SnapshotSpan) (ui:UIElement) =
        // Get the real offset so that the code lens are placed respectively to their content
        let offset =
            [0..line.Length - 1] |> Seq.tryFind (fun i -> not (Char.IsWhiteSpace (line.Start.Add(i).GetChar())))
            |> Option.defaultValue 0
        let realStart = line.Start.Add(offset)
        // Get the geometry which respects the changed height due to the SpaceAdornmentTag
        let geometry = 
            let g = view.TextViewLines.GetCharacterBounds(realStart)
            // WORKAROUND VS BUG, left cannot be zero if the offset is creater than zero!
            // Calling the method twice fixes this bug and ensures that all values are correct.
            if g.Left = 0. && offset > 0 then view.TextViewLines.GetCharacterBounds(realStart)
            else g
        Canvas.SetLeft(ui, geometry.Left)
        Canvas.SetTop(ui, geometry.Top)
    
    /// Creates the code lens ui elements for the specified text view line
    let getCodeLensUIElement (lens : CodeLens) (line : ITextViewLine) =
        let uiContext = SynchronizationContext.Current
        asyncMaybe {
            try
                let! view = view
                match lens.Computed, isNull lens.UiElement with
                // The line is already computed and has an existing UI element which is proved to be safe to use
                | true, false -> 
                    let textBox = lens.UiElement :?> TextBlock
                    layoutUIElementOnLine view line.Extent textBox
                    return Some textBox
                // The line is already computed but the UI element hasn't been created yet
                | _, _ ->
                    let! taggedText, navigation = lens.TaggedText
                    do! Async.SwitchToContext uiContext |> liftAsync
                    let textBox = new TextBlock(Width = view.ViewportWidth, Background = Brushes.Transparent, Opacity = 0.5, TextTrimming = TextTrimming.WordEllipsis)
                    DependencyObjectExtensions.SetDefaultTextProperties(textBox, formatMap.Value)
                    for text in taggedText do
                        let run = Documents.Run text.Text
                        DependencyObjectExtensions.SetTextProperties (run, layoutTagToFormatting text.Tag)
                        let inl =
                            match text with
                            | :? Layout.NavigableTaggedText as nav when navigation.IsTargetValid nav.Range ->
                                let h = Documents.Hyperlink(run, ToolTip = nav.Range.FileName)
                                h.Click.Add (fun _ -> navigation.NavigateTo nav.Range)
                                h :> Documents.Inline
                            | _ -> run :> _
                        textBox.Inlines.Add inl
                    textBox.Opacity <- 0.5
                    lens.Computed <- true
                    lens.UiElement <- textBox
                    if line.IsValid then
                        layoutUIElementOnLine view line.Extent textBox
                        let offset = 
                            view.TextViewLines.GetCharacterBounds(line.Start).Top - view.TextViewLines.GetCharacterBounds(view.TextViewLines.FirstVisibleLine.Start).Top
                        logInfof "Offset %A" offset
                        view.DisplayTextLineContainingBufferPosition(line.Start, offset, ViewRelativePosition.Top);
                    return Some textBox
                with e -> 
                    logErrorf "Unexpected exception occured! %A" e
                    return None
        } |> Async.map (fun ui ->
               match ui with
               | Some (Some ui) ->
                   Some ui
               | _ -> 
                   None)
    do buffer.Changed.AddHandler(fun _ e -> (self.BufferChanged e))

    member __.BufferChanged ___ =
        bufferChangedCts.Cancel() // Stop all ongoing async workflow. 
        bufferChangedCts.Dispose()
        bufferChangedCts <- new CancellationTokenSource()
        executeCodeLenseAsync () |> Async.Ignore |> RoslynHelpers.StartAsyncSafe bufferChangedCts.Token
    
    member __.SetView value = 
        view <- Some value
        codeLensLayer <- Some (value.GetAdornmentLayer "CodeLens")
    // Process the layout changed event from the ITextView
    member this.LayoutChanged (__) =
        try
            let uiContext = SynchronizationContext.Current
            let recentVisibleLineNumbers = Set [recentFirstVsblLineNmbr .. recentLastVsblLineNmbr]
            let firstVisibleLineNumber, lastVisibleLineNumber =
                match view with
                | None -> 0, 0
                | Some view ->
                    let first, last = 
                        view.TextViewLines.FirstVisibleLine, 
                        view.TextViewLines.LastVisibleLine
                    let buffer = buffer.CurrentSnapshot
                    buffer.GetLineNumberFromPosition(first.Start.Position),
                    buffer.GetLineNumberFromPosition(last.Start.Position)
            let visibleLineNumbers = Set [firstVisibleLineNumber .. lastVisibleLineNumber]
            let nonVisibleLineNumbers = Set.difference recentVisibleLineNumbers visibleLineNumbers
            let newVisibleLineNumbers = Set.difference visibleLineNumbers recentVisibleLineNumbers
        
            if nonVisibleLineNumbers.Count > 0 || newVisibleLineNumbers.Count > 0 then
                let buffer = buffer.CurrentSnapshot
                let view = view.Value
                for lineNumber in nonVisibleLineNumbers do
                    if lineNumber > 0 && lineNumber < buffer.LineCount then
                        let line = 
                            let l = buffer.GetLineFromLineNumber(lineNumber)
                            view.GetTextViewLineContainingBufferPosition(l.Start)
                        let tags = line.GetAdornmentTags(self)
                        match tags |> Seq.tryHead with
                        | None -> ()
                        | Some tag ->
                            let codeLens = tag :?> CodeLens
                            if not(isNull codeLens.UiElement) then
                                let ui = codeLens.UiElement
                                ui.Visibility <- Visibility.Collapsed
                for lineNumber in newVisibleLineNumbers do
                    let line = 
                            let l = buffer.GetLineFromLineNumber(lineNumber)
                            view.GetTextViewLineContainingBufferPosition(l.Start)
                    let tags = line.GetAdornmentTags(self)
                    match tags |> Seq.tryHead with
                    | None -> ()
                    | Some tag ->
                        let codeLens = tag :?> CodeLens
                        if not(isNull codeLens.UiElement) then
                            let ui = codeLens.UiElement
                            ui.Visibility <- Visibility.Visible
                            layoutUIElementOnLine view line.Extent ui
            // Save the new first and last visible lines for tracking
            recentFirstVsblLineNmbr <- firstVisibleLineNumber
            recentLastVsblLineNmbr <- lastVisibleLineNumber
            // We can cancel existing stuff because the algorithm supports abortion without any data loss
            layoutChangedCts.Cancel()
            layoutChangedCts.Dispose()
            layoutChangedCts <- new CancellationTokenSource()

            asyncMaybe {
                do! Async.SwitchToContext uiContext |> liftAsync
                do! Async.Sleep(5) |> liftAsync
                let! view = view
                let! layer = codeLensLayer
                if nonVisibleLineNumbers.Count > 0 || newVisibleLineNumbers.Count > 0 || updated then
                    let buffer = buffer.CurrentSnapshot
                    for lineNumber in visibleLineNumbers do
                        let line = 
                            let l = buffer.GetLineFromLineNumber(lineNumber)
                            view.GetTextViewLineContainingBufferPosition(l.Start)
                        let tags = line.GetAdornmentTags(self)
                        match tags |> Seq.tryHead with
                        | None -> ()
                        | Some tag ->
                            let codeLens = tag :?> CodeLens
                            if not(isNull codeLens.UiElement) then
                                let ui = codeLens.UiElement
                                ui.Visibility <- Visibility.Visible
                                layoutUIElementOnLine view line.Extent ui
                    updated <- false

                do! Async.Sleep(495) |> liftAsync

                let visibleSpan =
                    let first, last = 
                        view.TextViewLines.FirstVisibleLine, 
                        view.TextViewLines.LastVisibleLine
                    SnapshotSpan(first.Start, last.End)
                let customVisibleLines = view.TextViewLines.GetTextViewLinesIntersectingSpan visibleSpan
                let isLineVisible (line:ITextViewLine) = line.IsValid
                let linesToProcess = customVisibleLines |> Seq.filter isLineVisible

                for line in linesToProcess do
                    try
                        match line.GetAdornmentTags this |> Seq.tryHead with
                        | None -> ()
                        | Some tag ->
                            if not(addedAdornmentTags.ContainsKey(tag)) then
                                let codeLens = tag :?> CodeLens
                                if not codeLens.Computed then
                                    getCodeLensUIElement codeLens line |> Async.Ignore |> RoslynHelpers.StartAsyncSafe CancellationToken.None
                                else
                                    let da = DoubleAnimation(From = Nullable 0., To = Nullable 0.5, Duration = Duration(TimeSpan.FromSeconds 0.4))
                                    let! res = getCodeLensUIElement codeLens line |> liftAsync
                                    match res with
                                    | Some textBox ->
                                        logInfof "Adding adornment for tag %A" tag
                                        layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, Nullable(), 
                                            this, textBox, AdornmentRemovedCallback(fun _ _ -> ())) |> ignore
                                        textBox.BeginAnimation(UIElement.OpacityProperty, da)
                                        addedAdornmentTags.[codeLens] <- ()
                                    | None -> ()
                    with e -> logExceptionWithContext (e, "LayoutChanged, processing new visible lines")
            }
            |> Async.Ignore |> RoslynHelpers.StartAsyncSafe layoutChangedCts.Token
        with e -> logErrorf "%A" e
    
    member __.Tags = ConcurrentDictionary()

[<Export(typeof<IWpfTextViewCreationListener>)>]
[<Export(typeof<ITaggerProvider>)>]
[<TagType(typeof<CodeLensTag>)>]
[<ContentType(FSharpConstants.FSharpContentTypeName)>]
[<TextViewRole(PredefinedTextViewRoles.Document)>]
type internal CodeLensProvider  
    [<ImportingConstructor>]
    (
        textDocumentFactory: ITextDocumentFactoryService,
        checkerProvider: FSharpCheckerProvider,
        projectInfoManager: ProjectInfoManager,
        typeMap: Lazy<ClassificationTypeMap>,
        gotoDefinitionService: FSharpGoToDefinitionService
    ) as __ =

    let taggers = ResizeArray()
    
    let componentModel = Package.GetGlobalService(typeof<ComponentModelHost.SComponentModel>) :?> ComponentModelHost.IComponentModel
    let workspace = componentModel.GetService<VisualStudioWorkspace>()

    /// Returns an provider for the textView if already one has been created. Else create one.
    let getSuitableAdornmentProvider (buffer) =
        let res = taggers |> Seq.tryFind(fun (view, _) -> view = buffer)
        match res with
        | Some (_, res) -> res
        | None ->
            let documentId = 
                lazy (
                    match textDocumentFactory.TryGetTextDocument(buffer) with
                    | true, textDocument ->
                         Seq.tryHead (workspace.CurrentSolution.GetDocumentIdsWithFilePath(textDocument.FilePath))
                    | _ -> None
                    |> Option.get
                )


            let tagger = CodeLensTagger(workspace, documentId, buffer, checkerProvider.Checker, projectInfoManager, typeMap, gotoDefinitionService)
            taggers.Add((buffer, tagger))
            tagger
    [<Export(typeof<AdornmentLayerDefinition>); Name("CodeLens");
      Order(Before = PredefinedAdornmentLayers.Text);
      TextViewRole(PredefinedTextViewRoles.Document)>]

    member val CodeLensAdornmentLayerDefinition : AdornmentLayerDefinition = null with get, set

    interface IWpfTextViewCreationListener with
        
        override __.TextViewCreated view =
            let tagger = getSuitableAdornmentProvider view.TextBuffer
            tagger.SetView view
            view.LayoutChanged.AddHandler(fun _ e -> tagger.LayoutChanged e)
            // The view has been initialized. Notify that we can now theoretically display CodeLens
            // Temporarily removed, eventually needed again!  
            ()
             

    interface ITaggerProvider with
        override __.CreateTagger(buffer) = box (getSuitableAdornmentProvider buffer) :?> _
