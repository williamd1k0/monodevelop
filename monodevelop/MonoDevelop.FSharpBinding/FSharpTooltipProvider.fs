﻿// --------------------------------------------------------------------------------------
// Provides tool tips with F# hints for MonoDevelop
// (this file implements MonoDevelop interfaces and calls 'LanguageService')
// --------------------------------------------------------------------------------------
namespace MonoDevelop.FSharp

open System
open System.Collections.Generic
open FSharp.CompilerBinding
open Mono.TextEditor
open MonoDevelop.Core
open MonoDevelop.Ide
open MonoDevelop.Ide.CodeCompletion
open Gdk
open MonoDevelop.Components
open Microsoft.FSharp.Compiler.SourceCodeServices

[<AutoOpen>]
module FSharpTypeExt =
    let isOperator (name: string) =
            if name.StartsWith "( " && name.EndsWith " )" && name.Length > 4 then
                name.Substring (2, name.Length - 4) |> String.forall (fun c -> c <> ' ')
            else false

    let rec getAbbreviatedType (fsharpType: FSharpType) =
        if fsharpType.IsAbbreviation then
            let typ = fsharpType.AbbreviatedType
            if typ.HasTypeDefinition then getAbbreviatedType typ
            else fsharpType
        else fsharpType

    let isReferenceCell (fsharpType: FSharpType) = 
        let ty = getAbbreviatedType fsharpType
        ty.HasTypeDefinition && ty.TypeDefinition.IsFSharpRecord && ty.TypeDefinition.FullName = "Microsoft.FSharp.Core.FSharpRef`1"
    
    type FSharpType with
        member x.IsReferenceCell =
            isReferenceCell x

type XmlDoc =
| Full of string
| Lookup of key: string * filename: string option
| EmptyDoc

type ToolTips =
| ToolTip of string * XmlDoc * TextSegment
| EmptyTip

[<AutoOpen>]
module NewTooltips =
    let getColourScheme () =
        Highlighting.SyntaxModeService.GetColorStyle (IdeApp.Preferences.ColorScheme)

    let hl str (style: Highlighting.ChunkStyle) =
        let color = getColourScheme().GetForeground (style) |> GtkUtil.ToGdkColor
        let  colorString = HelperMethods.GetColorString (color)
        "<span foreground=\"" + colorString + "\">" + str + "</span>"

    /// Add two strings with a space between
    let (++) a b = a + " " + b

    let getSegFromSymbolUse (editor:TextEditor) (symbolUse:FSharpSymbolUse)  =
        let startOffset = editor.Document.LocationToOffset(symbolUse.RangeAlternate.StartLine, symbolUse.RangeAlternate.StartColumn)
        let endOffset = editor.Document.LocationToOffset(symbolUse.RangeAlternate.EndLine, symbolUse.RangeAlternate.EndColumn)
        TextSegment.FromBounds(startOffset, endOffset)

    let getSummaryFromSymbol (symbolUse:FSharpSymbolUse) =
        let xmlDoc, xmlDocSig = 
            match symbolUse.Symbol with
            | :? FSharpMemberFunctionOrValue as func -> func.XmlDoc, func.XmlDocSig
            | :? FSharpEntity as fse -> fse.XmlDoc, fse.XmlDocSig
            | :? FSharpField as fsf -> fsf.XmlDoc, fsf.XmlDocSig
            | _ -> ResizeArray() :> IList<_>, ""

        if xmlDoc.Count > 0 then Full (String.Join( "\n", xmlDoc |> Seq.map GLib.Markup.EscapeText))
        else
            if String.IsNullOrWhiteSpace xmlDocSig then XmlDoc.EmptyDoc
            else Lookup(xmlDocSig, symbolUse.Symbol.Assembly.FileName)

/// Resolves locations to tooltip items, and orchestrates their display.
/// We resolve language items to an NRefactory symbol.
type FSharpTooltipProvider() = 
    inherit Mono.TextEditor.TooltipProvider()

    // Keep the last result and tooltip window cached
    let mutable lastResult = None : TooltipItem option

    static let mutable lastWindow = None
   
    let killTooltipWindow() =
       match lastWindow with
       | Some(w:TooltipInformationWindow) -> w.Destroy()
       | None -> ()

    override x.GetItem (editor, offset) =
      try
        let fileName = IdeApp.Workbench.ActiveDocument.FileName.FullPath.ToString()
        let extEditor = editor :?> MonoDevelop.SourceEditor.ExtensibleTextEditor 
        let docText = editor.Text
        if docText = null || offset >= docText.Length || offset < 0 then null else
        let projFile, files, args, framework = MonoDevelop.getCheckerArgs(extEditor.Project, fileName)
        let tyResOpt =
            MDLanguageService.Instance.GetTypedParseResultWithTimeout
                 (projFile,
                  fileName, 
                  docText, 
                  files,
                  args,
                  AllowStaleResults.MatchingSource,
                  ServiceSettings.blockingTimeout,
                  framework) |> Async.RunSynchronously
        LoggingService.LogInfo "TooltipProvider: Getting tool tip"
        match tyResOpt with
        | None -> LoggingService.LogWarning "TooltipProvider: ParseAndCheckResults not found"
                  null
        | Some tyRes ->
        // Get tool-tip from the language service
        let line, col, lineStr = MonoDevelop.getLineInfoFromOffset(offset, editor.Document)
        let tip, symbolUse = async {let! tooltip = tyRes.GetToolTip(line, col, lineStr)
                                    let! symbol = tyRes.GetSymbol(line, col, lineStr)
                                    return tooltip, symbol} |> Async.RunSynchronously

        let typeTip =
            match symbolUse with
            | Some symbolUse -> 
                match symbolUse.Symbol with
                | :? FSharpEntity as fse ->
                    try
                        let cs = getColourScheme()

                        let displayName = fse.DisplayName

                        let modifier = match fse.Accessibility with
                                       | a when a.IsInternal -> hl "internal " cs.KeywordTypes
                                       | a when a.IsPrivate -> hl "private " cs.KeywordTypes
                                       | _ -> ""

                        let attributes =
                            // Maybe search for modifier attributes like abstract, sealed and append them above the type:
                            // [<Abstract>]
                            // type Test = ...
                            String.Join ("\n", fse.Attributes 
                                               |> Seq.map (fun a -> let name = a.AttributeType.DisplayName.Replace("Attribute", "")
                                                                    let parameters = String.Join(", ",  a.ConstructorArguments |> Seq.filter (fun ca -> ca :? string ) |> Seq.cast<string>)
                                                                    if String.IsNullOrWhiteSpace parameters then "[<" + name + ">]"
                                                                    else "[<" + name + "( " + parameters + " )" + ">]" ) 
                                               |> Seq.toArray)

                        let signature =
                            let typeName =
                                match fse with
                                | _ when fse.IsFSharpModule -> "module"
                                | _ when fse.IsEnum         -> "enum"
                                | _ when fse.IsValueType    -> "struct"
                                | _                         -> "type"

                            let enumtip () =
                                hl " =" cs.KeywordOperators + "\n" + 
                                hl "| " cs.KeywordOperators +
                                (fse.FSharpFields
                                |> Seq.filter (fun f -> not f.IsCompilerGenerated)
                                |> Seq.map (fun field -> field.Name) //TODO Fix FSC to expose enum filed literals: field.Name + (hl " = " cs.KeywordOperators) + hl field.LiteralValue cs.UserTypesValueTypes*)
                                |> String.concat ("\n" + hl "| " cs.KeywordOperators) )
               

                            let uniontip () = 
                                hl " =" cs.KeywordOperators + "\n" + 
                                hl "| " cs.KeywordOperators +
                                (fse.UnionCases 
                                |> Seq.map (fun unionCase -> 
                                                if unionCase.UnionCaseFields.Count > 0 then
                                                   let typeList =
                                                      unionCase.UnionCaseFields
                                                      |> Seq.map (fun unionField -> unionField.Name + (hl " : " cs.KeywordOperators) + hl (unionField.FieldType.Format symbolUse.DisplayContext) cs.UserTypes ) 
                                                      |> String.concat (hl " * " cs.KeywordOperators)
                                                   unionCase.Name + (hl " of " cs.KeywordTypes) + typeList
                                                 else unionCase.Name)

                                |> String.concat ("\n" + hl "| " cs.KeywordOperators) )
                                                     
                            modifier +
                            hl typeName cs.KeywordTypes ++ 
                            hl displayName cs.UserTypes +
                            (if fse.IsFSharpUnion then uniontip()
                             elif fse.IsEnum then enumtip() 
                             else "") +
                            "\n\nFull name: " + fse.FullName

                        ToolTip(signature, getSummaryFromSymbol symbolUse, getSegFromSymbolUse editor symbolUse)
                    with exn -> ToolTips.EmptyTip

                | :? FSharpMemberFunctionOrValue as func ->
                    try
                    if func.CompiledName = ".ctor" then 
                        if func.EnclosingEntity.IsValueType || func.EnclosingEntity.IsEnum then
                            //TODO: Add ValueType
                            ToolTips.EmptyTip
                        else
                            //TODO: Add ReferenceType
                            ToolTips.EmptyTip

                    elif func.FullType.IsFunctionType && not func.IsPropertyGetterMethod && not func.IsPropertySetterMethod && not symbolUse.IsFromComputationExpression then 
                        if isOperator func.DisplayName then
                            //TODO: Add operators, the text will look like:
                            // val ( symbol ) : x:string -> y:string -> string
                            // Full name: Name.Blah.( symbol )
                            // Note: (In the current compiler tooltips a closure defined symbol will be missing the named types and the full name)
                            ToolTips.EmptyTip
                        else
                            //TODO: Add closure/nested functions
                            if not func.IsModuleValueOrMember then
                                //represents a closure or nested function
                                ToolTips.EmptyTip
                            else
                                let signature =

                                    let cs = getColourScheme()

                                    let backupSignature = func.FullType.Format symbolUse.DisplayContext
                                    let argInfos =
                                        func.CurriedParameterGroups 
                                        |> Seq.map Seq.toList 
                                        |> Seq.toList 

                                    let retType = hl (GLib.Markup.EscapeText(func.ReturnParameter.Type.Format symbolUse.DisplayContext)) cs.UserTypes

                                    //example of building up the parameters using Display name and Type
                                    let signature =
                                        let padLength = argInfos |> List.concat |> List.map (fun p -> p.DisplayName.Length) |> List.max
                                        let parameters = 
                                            match argInfos with
                                            | [] -> retType
                                            | [[single]] -> "   " + single.DisplayName + hl ": " cs.KeywordOperators + hl (GLib.Markup.EscapeText(single.Type.Format symbolUse.DisplayContext)) cs.UserTypes
                                            | many ->
                                                many
                                                |> List.map(fun listOfParams ->
                                                                listOfParams
                                                                |> List.map(fun (p:FSharpParameter) ->
                                                                                "   " + p.DisplayName.PadRight (padLength) + hl ": " cs.KeywordOperators + hl (GLib.Markup.EscapeText(p.Type.Format symbolUse.DisplayContext)) cs.UserTypes)
                                                                                |> String.concat (hl " * " cs.KeywordOperators + "\n"))
                                                |> String.concat (hl " ->" cs.KeywordOperators + "\n")
                                        parameters + hl ("\n   " + (String.replicate (padLength-1) " ") +  "-> ") cs.KeywordOperators + retType

                                    let modifiers = 
                                        if func.IsMember then 
                                            if func.IsInstanceMember then
                                                if func.IsDispatchSlot then "abstract member"
                                                else "member"
                                            else "static member"
                                        else
                                            if func.InlineAnnotation = FSharpInlineAnnotation.AlwaysInline then "inline val"
                                            elif func.IsInstanceMember then "val"
                                            else "val" //does this need to be static prefixed?

                                    hl modifiers cs.KeywordTypes ++ func.DisplayName + hl " : " cs.KeywordOperators + "\n" + signature

                                ToolTip(signature, getSummaryFromSymbol symbolUse, getSegFromSymbolUse editor symbolUse)                            

                    else
                        //val name : Type
                        let signature =
                                let cs = getColourScheme()
                                let retType = hl (GLib.Markup.EscapeText(func.ReturnParameter.Type.Format symbolUse.DisplayContext)) cs.UserTypes
                                let prefix = 
                                    if func.IsMutable then hl "val" cs.KeywordTypes ++ hl "mutable" cs.KeywordModifiers
                                    else hl "val" cs.KeywordTypes
                                prefix ++ func.DisplayName ++ hl ":" cs.KeywordOperators ++ retType

                        ToolTip(signature, getSummaryFromSymbol symbolUse, getSegFromSymbolUse editor symbolUse)
                    with exn -> ToolTips.EmptyTip

                | _ -> ToolTips.EmptyTip

            | None -> ToolTips.EmptyTip
        
        //As the new tooltips are unfinished we match ToolTip here to use the new tooltips and anything else to run through the old tooltip system
        // In the section above we return EmptyTip for any tooltips symbols that have not yet ben finished
        match typeTip with
        | ToolTip(signature, summary, textSeg) -> TooltipItem ((signature, summary), textSeg)
        | EmptyTip ->

        match tip with
        | None -> LoggingService.LogWarning "TooltipProvider: TootipText not returned"
                  null
        | Some (ToolTipText(elems),_) when elems |> List.forall (function ToolTipElementNone -> true | _ -> false) -> 
            LoggingService.LogWarning "TooltipProvider: No data found"
            null
        | Some(tiptext,(col1,col2)) -> 
            LoggingService.LogInfo "TooltipProvider: Got data"
            //check to see if the last result is the same tooltipitem, if so return the previous tooltipitem
            match lastResult with
            | Some(tooltipItem) when
                tooltipItem.Item :? ToolTipText && 
                tooltipItem.Item :?> ToolTipText = tiptext && 
                tooltipItem.ItemSegment = TextSegment(editor.LocationToOffset (line, col1 + 1), col2 - col1) -> tooltipItem
            //If theres no match or previous cached result generate a new tooltipitem
            | Some(_) | None -> 
                let line = editor.Document.OffsetToLineNumber offset
                let segment = TextSegment(editor.LocationToOffset (line, col1 + 1), col2 - col1)
                let tooltipItem = TooltipItem (tiptext, segment)
                lastResult <- Some(tooltipItem)
                tooltipItem
      with exn -> LoggingService.LogError ("TooltipProvider: Error retrieving tooltip", exn)
                  null

    override x.CreateTooltipWindow (editor, offset, modifierState, item) = 
        let doc = IdeApp.Workbench.ActiveDocument
        if (doc = null) then null else
        //At the moment as the new tooltips are unfinished we have two types here
        // ToolTipText for the old tooltips and (string * XmlDoc) for the new tooltips
        match item.Item with 
        | :? ToolTipText as titem ->
            let tooltip = TipFormatter.formatTip(titem)
            let (signature, comment) = 
                match tooltip with
                | [signature,comment] -> signature,comment
                //With multiple tips just take the head.  
                //This shouldnt happen anyway as we split them in the resolver provider
                | multiple -> multiple |> List.head
            //dont show a tooltip if there is no content
            if String.IsNullOrEmpty(signature) then null 
            else            
                let result = new TooltipInformationWindow(ShowArrow = true)
                let toolTipInfo = new TooltipInformation(SignatureMarkup = signature)
                if not (String.IsNullOrEmpty(comment)) then toolTipInfo.SummaryMarkup <- comment
                result.AddOverload(toolTipInfo)
                result.RepositionWindow ()                  
                result :> _

        | :? (string * XmlDoc) as tip -> 
            let signature, xmldoc = tip
            let result = new TooltipInformationWindow(ShowArrow = true)
            let toolTipInfo = new TooltipInformation(SignatureMarkup = signature)
            match xmldoc with
            | Full(summary) -> toolTipInfo.SummaryMarkup <- summary
            | Lookup(key, filename) ->
                match filename with
                | Some f ->
                    let markup = TipFormatter.findDocForEntity(f, key)
                    match markup with
                    | Some summaryText -> toolTipInfo.SummaryMarkup <- Tooltips.getTooltip Styles.simpleMarkup summaryText
                    | _ -> ()
                | _ -> ()
            | _ -> ()

            result.AddOverload(toolTipInfo)
            result.RepositionWindow ()                  
            result :> _

        | _ -> LoggingService.LogError "TooltipProvider: Type mismatch, not a FSharpLocalResolveResult"
               null
    
    override x.ShowTooltipWindow (editor, offset, modifierState, mouseX, mouseY, item) =
        match (lastResult, lastWindow) with
        | Some(lastRes), Some(lastWin) when item.Item = lastRes.Item && lastWin.IsRealized ->
            lastWin :> _                   
        | _ -> killTooltipWindow()
               match x.CreateTooltipWindow (editor, offset, modifierState, item) with
               | :? TooltipInformationWindow as tipWindow ->
                   let positionWidget = editor.TextArea
                   let region = item.ItemSegment.GetRegion(editor.Document)
                   let p1, p2 = editor.LocationToPoint(region.Begin), editor.LocationToPoint(region.End)
                   let caret = Gdk.Rectangle (int p1.X - positionWidget.Allocation.X, 
                                              int p2.Y - positionWidget.Allocation.Y, 
                                              int (p2.X - p1.X), 
                                              int editor.LineHeight)
                   //For debug this is usful for visualising the tooltip location
                   // editor.SetSelection(item.ItemSegment.Offset, item.ItemSegment.EndOffset)
               
                   tipWindow.ShowPopup(positionWidget, caret, MonoDevelop.Components.PopupPosition.Top)
                   tipWindow.EnterNotifyEvent.Add(fun _ -> editor.HideTooltip (false))
                   //cache last window shown
                   lastWindow <- Some(tipWindow)
                   lastResult <- Some(item)
                   tipWindow :> _
               | _ -> LoggingService.LogError "TooltipProvider: Type mismatch, not a TooltipInformationWindow"
                      null
            
    interface IDisposable with
        member x.Dispose() = killTooltipWindow()
