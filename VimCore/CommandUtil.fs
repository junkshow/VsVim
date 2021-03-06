﻿#light

namespace Vim
open Vim.Modes
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Outlining

type internal CommandUtil 
    (
        _buffer : IVimBuffer,
        _operations : ICommonOperations,
        _statusUtil : IStatusUtil,
        _undoRedoOperations : IUndoRedoOperations,
        _smartIndentationService : ISmartIndentationService,
        _foldManager : IFoldManager
    ) =

    let _textView = _buffer.TextView
    let _textBuffer = _textView.TextBuffer
    let _bufferGraph = _textView.BufferGraph
    let _motionUtil = _buffer.MotionUtil
    let _registerMap = _buffer.RegisterMap
    let _markMap = _buffer.MarkMap
    let _vimData = _buffer.VimData
    let _localSettings = _buffer.LocalSettings
    let _globalSettings = _localSettings.GlobalSettings
    let _vim = _buffer.Vim
    let _vimHost = _vim.VimHost
    let _searchService = _vim.SearchService
    let _wordNavigator = _buffer.WordNavigator
    let _jumpList = _buffer.JumpList
    let _editorOperations = _operations.EditorOperations
    let _options = _operations.EditorOptions

    let mutable _inRepeatLastChange = false

    /// The column of the caret
    member x.CaretColumn = SnapshotPointUtil.GetColumn x.CaretPoint

    /// The SnapshotPoint for the caret
    member x.CaretPoint = TextViewUtil.GetCaretPoint _textView

    /// The ITextSnapshotLine for the caret
    member x.CaretLine = TextViewUtil.GetCaretLine _textView

    /// The line number for the caret
    member x.CaretLineNumber = x.CaretLine.LineNumber

    /// The SnapshotLineRange for the caret line
    member x.CaretLineRange = x.CaretLine |> SnapshotLineRangeUtil.CreateForLine

    /// The SnapshotPoint and ITextSnapshotLine for the caret
    member x.CaretPointAndLine = TextViewUtil.GetCaretPointAndLine _textView

    /// The current ITextSnapshot instance for the ITextBuffer
    member x.CurrentSnapshot = _textBuffer.CurrentSnapshot

    /// Calculate the new RegisterValue for the provided one for put with indent
    /// operations.
    member x.CalculateIdentStringData (registerValue : RegisterValue) =

        // Get the indent string to apply to the lines which are indented
        let indent = 
            x.CaretLine.GetText()
            |> Seq.takeWhile CharUtil.IsBlank
            |> StringUtil.ofCharSeq
            |> _operations.NormalizeBlanks

        // Adjust the indentation on a given line of text to have the indentation
        // previously calculated
        let adjustTextLine (textLine : TextLine) =
            let oldIndent = textLine.Text |> Seq.takeWhile CharUtil.IsBlank |> StringUtil.ofCharSeq
            let text = indent + (textLine.Text.Substring(oldIndent.Length))
            { textLine with Text = text }

        // Really a put after with indent is just a normal put after of the adjusted 
        // register value.  So adjust here and forward on the magic
        let stringData = 
            let stringData = registerValue.StringData
            match stringData with 
            | StringData.Block _ -> 
                // Block values don't participate in indentation of this manner
                stringData 
            | StringData.Simple text ->
                match registerValue.OperationKind with
                | OperationKind.CharacterWise ->
    
                    // We only change lines after the first.  So break it into separate lines
                    // fix their indent and then produce the new value.
                    let lines = TextLine.GetTextLines text
                    let head = lines.Head
                    let rest = lines.Rest |> Seq.map adjustTextLine
                    let text = 
                        let all = Seq.append (Seq.singleton head) rest
                        TextLine.CreateString all
                    StringData.Simple text

                | OperationKind.LineWise -> 

                    // Change every line for a line wise operation
                    text
                    |> TextLine.GetTextLines
                    |> Seq.map adjustTextLine
                    |> TextLine.CreateString
                    |> StringData.Simple

        RegisterValue.String (stringData, registerValue.OperationKind)

    /// Calculate the VisualSpan value for the associated ITextBuffer given the 
    /// StoreVisualSpan value
    member x.CalculateVisualSpan stored =

        match stored with
        | StoredVisualSpan.Line count -> 
            // Repeating a LineWise operation just creates a span with the same 
            // number of lines as the original operation
            let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
            VisualSpan.Line range

        | StoredVisualSpan.Character (endLineOffset, endOffset) -> 
            // Repeating a CharecterWise span starts from the caret position.  There
            // are 2 cases to consider
            //
            //  1. Single Line: endOffset is the offset from the caret
            //  2. Multi Line: endOffset is the offset from the last line
            let startPoint = x.CaretPoint

            /// Calculate the end point being careful not to go past the end of the buffer
            let endPoint = 
                if 0 = endLineOffset then
                    let column = SnapshotPointUtil.GetColumn x.CaretPoint
                    SnapshotLineUtil.GetOffsetOrEnd x.CaretLine (column + endOffset)
                else
                    let endLineNumber = x.CaretLine.LineNumber + endLineOffset
                    match SnapshotUtil.TryGetLine x.CurrentSnapshot endLineNumber with
                    | None -> SnapshotUtil.GetEndPoint x.CurrentSnapshot
                    | Some endLine -> SnapshotLineUtil.GetOffsetOrEnd endLine endOffset

            let span = SnapshotSpan(startPoint, endPoint)
            VisualSpan.Character span
        | StoredVisualSpan.Block (length, count) ->
            // Need to rehydrate spans of length 'length' on 'count' lines from the 
            // current caret position
            let column = x.CaretColumn
            let col = 
                SnapshotUtil.GetLines x.CurrentSnapshot x.CaretLineNumber Path.Forward
                |> Seq.truncate count
                |> Seq.map (fun line ->
                    let startPoint = 
                        if column >= line.Length then line.End 
                        else line.Start.Add(column)
                    let endPoint = 
                        if startPoint.Position + length >= line.End.Position then line.End 
                        else startPoint.Add(length)
                    SnapshotSpan(startPoint, endPoint))
                |> NonEmptyCollectionUtil.OfSeq
                |> Option.get
            VisualSpan.Block col

    /// Change the characters in the given span via the specified change kind
    member x.ChangeCaseSpanCore kind (editSpan : EditSpan) =

        let func = 
            match kind with
            | ChangeCharacterKind.Rot13 -> CharUtil.ChangeRot13
            | ChangeCharacterKind.ToLowerCase -> CharUtil.ToLower
            | ChangeCharacterKind.ToUpperCase -> CharUtil.ToUpper
            | ChangeCharacterKind.ToggleCase -> CharUtil.ChangeCase

        use edit = _textBuffer.CreateEdit()
        editSpan.Spans
        |> Seq.map (SnapshotSpanUtil.GetPoints Path.Forward)
        |> Seq.concat
        |> Seq.filter (fun p -> CharUtil.IsLetter (p.GetChar()))
        |> Seq.iter (fun p ->
            let change = func (p.GetChar()) |> StringUtil.ofChar
            edit.Replace(p.Position, 1, change) |> ignore)
        edit.Apply() |> ignore

    /// Change the caret line via the specified ChangeCharacterKind.
    member x.ChangeCaseCaretLine kind =

        // The caret should be positioned on the first non-blank space in 
        // the line.  If the line is completely blank the caret should
        // not be moved.  Caret should be in the same place for undo / redo
        // so move before and inside the transaction
        let position = 
            x.CaretLine
            |> SnapshotLineUtil.GetPoints Path.Forward
            |> Seq.skipWhile SnapshotPointUtil.IsWhiteSpace
            |> Seq.map SnapshotPointUtil.GetPosition
            |> SeqUtil.tryHeadOnly

        let maybeMoveCaret () =
            match position with
            | Some position -> TextViewUtil.MoveCaretToPosition _textView position
            | None -> ()

        maybeMoveCaret()
        x.EditWithUndoTransaciton "Change" (fun () ->
            x.ChangeCaseSpanCore kind (EditSpan.Single x.CaretLine.Extent)
            maybeMoveCaret())

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Change the case of the specified motion
    member x.ChangeCaseMotion kind (result : MotionResult) =

        // The caret should be placed at the start of the motion for both
        // undo / redo so move before and inside the transaction
        TextViewUtil.MoveCaretToPoint _textView result.Span.Start
        x.EditWithUndoTransaciton "Change" (fun () ->
            x.ChangeCaseSpanCore kind result.EditSpan
            TextViewUtil.MoveCaretToPosition _textView result.Span.Start.Position)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Change the case of the current caret point
    member x.ChangeCaseCaretPoint kind count =

        // The caret should be placed after the caret point but only 
        // for redo.  Undo should move back to the current position so 
        // don't move until inside the transaction
        x.EditWithUndoTransaciton "Change" (fun () ->

            let span = 
                let endPoint = SnapshotLineUtil.GetOffsetOrEnd x.CaretLine (x.CaretColumn + count)
                SnapshotSpan(x.CaretPoint, endPoint)

            let editSpan = EditSpan.Single span
            x.ChangeCaseSpanCore kind editSpan

            // Move the caret but make sure to respect the 'virtualedit' option
            let point = SnapshotPoint(x.CurrentSnapshot, span.End.Position)
            _operations.MoveCaretToPointAndCheckVirtualSpace point)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Change the case of the selected text.  
    member x.ChangeCaseVisual kind (visualSpan : VisualSpan) = 

        // The caret should be positioned at the start of the VisualSpan for both 
        // undo / redo so move it before and inside the transaction
        let point = visualSpan.Start
        let moveCaret () = TextViewUtil.MoveCaretToPosition _textView point.Position
        moveCaret()
        x.EditWithUndoTransaciton "Change" (fun () ->
            x.ChangeCaseSpanCore kind visualSpan.EditSpan
            moveCaret())

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Delete the specified motion and enter insert mode
    member x.ChangeMotion register (result : MotionResult) = 

        // This command has legacy / special case behavior for forward word motions.  It will 
        // not delete any trailing whitespace in the span if the motion is created for a forward 
        // word motion. This behavior is detailed in the :help WORD section of the gVim 
        // documentation and is likely legacy behavior coming from the original vi 
        // implementation.  A larger discussion thread is available here
        // http://groups.google.com/group/vim_use/browse_thread/thread/88b6499bbcb0878d/561dfe13d3f2ef63?lnk=gst&q=whitespace+cw#561dfe13d3f2ef63

        let span = 
            if result.IsAnyWordMotion && result.IsForward then
                let point = 
                    result.Span
                    |> SnapshotSpanUtil.GetPoints Path.Backward
                    |> Seq.tryFind (fun x -> x.GetChar() |> CharUtil.IsWhiteSpace |> not)
                match point with 
                | Some(p) -> 
                    let endPoint = 
                        p
                        |> SnapshotPointUtil.TryAddOne 
                        |> OptionUtil.getOrDefault (SnapshotUtil.GetEndPoint (p.Snapshot))
                    SnapshotSpan(result.Span.Start, endPoint)
                | None -> result.Span
            else
                result.Span

        // Use an undo transaction to preserve the caret position.  It should be at the start
        // of the span being deleted before and after the undo / redo so move it before and 
        // after the delete occurs
        TextViewUtil.MoveCaretToPoint _textView span.Start
        let commandResult = 
            x.EditWithLinkedChange "Change" (fun () ->
                _textBuffer.Delete(span.Span) |> ignore
                TextViewUtil.MoveCaretToPosition _textView span.Start.Position)

        // Now that the delete is complete update the register
        let value = RegisterValue.String (StringData.OfSpan span, result.OperationKind)
        _registerMap.SetRegisterValue register RegisterOperation.Delete value

        commandResult

    /// Delete 'count' lines and begin insert mode.  The documentation of this command 
    /// and behavior are a bit off.  It's documented like it behaves lke 'dd + insert mode' 
    /// but behaves more like ChangeTillEndOfLine but linewise and deletes the entire
    /// first line
    member x.ChangeLines count register = 

        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        x.ChangeLinesCore range register

    /// Core routine for changing a set of lines in the ITextBuffer.  This is the backing function
    /// for changing lines in both normal and visual mode
    member x.ChangeLinesCore (range : SnapshotLineRange) register = 

        // Caret position for the undo operation depends on the number of lines which are in
        // range being deleted.  If there is a single line then we position it before the first
        // non space / tab character in the first line.  If there is more than one line then we 
        // position it at the equivalent location in the second line.  
        // 
        // There appears to be no logical reason for this behavior difference but it exists
        let point = 
            let line = 
                if range.Count = 1 then
                    range.StartLine
                else
                    SnapshotUtil.GetLine range.Snapshot (range.StartLineNumber + 1)
            line
            |> SnapshotLineUtil.GetPoints Path.Forward
            |> Seq.skipWhile SnapshotPointUtil.IsBlank
            |> SeqUtil.tryHeadOnly
        match point with
        | None -> ()
        | Some point -> TextViewUtil.MoveCaretToPoint _textView point

        // Start an edit transaction to get the appropriate undo / redo behavior for the 
        // caret movement after the edit.
        x.EditWithLinkedChange "ChangeLines" (fun () -> 

            // Actually delete the text and position the caret
            _textBuffer.Delete(range.Extent.Span) |> ignore
            x.MoveCaretToDeletedLineStart range.StartLine

            // Update the register now that the operation is complete.  Register value is odd here
            // because we really didn't delete linewise but it's required to be a linewise 
            // operation.  
            let value = range.Extent.GetText() + System.Environment.NewLine
            let value = RegisterValue.OfString value OperationKind.LineWise
            _registerMap.SetRegisterValue register RegisterOperation.Delete value)

    /// Delete the selected lines and begin insert mode (implements the 'S', 'C' and 'R' visual
    /// mode commands.  This is very similar to DeleteLineSelection except that block deletion
    /// can be special cased depending on the command it's used in
    member x.ChangeLineSelection register (visualSpan : VisualSpan) specialCaseBlock =

        // The majority of cases simply delete a SnapshotLineRange directly.  Handle that here
        let deleteRange (range : SnapshotLineRange) = 

            // In an undo the caret position has 2 cases.
            //  - Single line range: Start of the first line
            //  - Multiline range: Start of the second line.
            let point = 
                if range.Count = 1 then 
                    range.StartLine.Start
                else 
                    let next = SnapshotUtil.GetLine range.Snapshot (range.StartLineNumber + 1)
                    next.Start
            TextViewUtil.MoveCaretToPoint _textView point

            let commandResult = x.EditWithLinkedChange "ChangeLines" (fun () -> 
                _textBuffer.Delete(range.Extent.Span) |> ignore
                x.MoveCaretToDeletedLineStart range.StartLine)

            (EditSpan.Single range.Extent, commandResult)

        // The special casing of block deletion is handled here
        let deleteBlock (col : NonEmptyCollection<SnapshotSpan>) = 

            // First step is to change the SnapshotSpan instances to extent from the start to the
            // end of the current line 
            let col = col |> NonEmptyCollectionUtil.Map (fun span -> 
                let line = SnapshotPointUtil.GetContainingLine span.Start
                SnapshotSpan(span.Start, line.End))

            // Caret should be positioned at the start of the span for undo
            TextViewUtil.MoveCaretToPoint _textView col.Head.Start

            let commandResult = x.EditWithLinkedChange "ChangeLines" (fun () ->
                let edit = _textBuffer.CreateEdit()
                col |> Seq.iter (fun span -> edit.Delete(span.Span) |> ignore)
                edit.Apply() |> ignore

                TextViewUtil.MoveCaretToPosition _textView col.Head.Start.Position)

            (EditSpan.Block col, commandResult)

        // Dispatch to the appropriate type of edit
        let editSpan, commandResult = 
            match visualSpan with 
            | VisualSpan.Character span -> 
                span |> SnapshotLineRangeUtil.CreateForSpan |> deleteRange
            | VisualSpan.Line range -> 
                deleteRange range
            | VisualSpan.Block col -> 
                if specialCaseBlock then deleteBlock col 
                else visualSpan.EditSpan.OverarchingSpan |> SnapshotLineRangeUtil.CreateForSpan |> deleteRange

        let value = RegisterValue.String (StringData.OfEditSpan editSpan, OperationKind.LineWise)
        _registerMap.SetRegisterValue register RegisterOperation.Delete value

        commandResult

    /// Delete till the end of the line and start insert mode
    member x.ChangeTillEndOfLine count register =

        // The actual text edit portion of this operation is identical to the 
        // DeleteTillEndOfLine operation.  There is a difference though in the
        // positioning of the caret.  DeleteTillEndOfLine needs to consider the virtual
        // space settings since it remains in normal mode but change does not due
        // to it switching to insert mode
        let caretPosition = x.CaretPoint.Position
        x.EditWithLinkedChange "ChangeTillEndOfLine" (fun () ->
            x.DeleteTillEndOfLineCore count register

            // Move the caret back to it's original position.  Don't consider virtual
            // space here since we're switching to insert mode
            let point = SnapshotPoint(x.CurrentSnapshot, caretPosition)
            _operations.MoveCaretToPoint point)

    /// Delete the selected text in Visual Mode and begin insert mode with a linked
    /// transaction. 
    member x.ChangeSelection register (visualSpan : VisualSpan) = 

        // For block and character modes the change selection command is simply a 
        // delete of the span and move into insert mode.  
        let editSelection () = 
            // Caret needs to be positioned at the front of the span in undo so move it
            // before we create the transaction
            TextViewUtil.MoveCaretToPoint _textView visualSpan.Start
            x.EditWithLinkedChange "ChangeSelection" (fun() -> 
                x.DeleteSelection register visualSpan |> ignore)

        match visualSpan with
        | VisualSpan.Character _ -> editSelection()
        | VisualSpan.Block _ ->  editSelection()
        | VisualSpan.Line range -> x.ChangeLinesCore range register

    /// Close a single fold under the caret
    member x.CloseFoldInSelection (visualSpan : VisualSpan) =
        let range = visualSpan.LineRange
        let offset = range.StartLineNumber
        for i = 0 to range.Count - 1 do
            let line = SnapshotUtil.GetLine x.CurrentSnapshot (offset + 1)
            _foldManager.CloseFold line.Start 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Close 'count' folds under the caret
    member x.CloseFoldUnderCaret count =
        _foldManager.CloseFold x.CaretPoint count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Close all folds under the caret
    member x.CloseAllFoldsUnderCaret () =
        let span = SnapshotSpan(x.CaretPoint, 0)
        _foldManager.CloseAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Close all folds in the selection
    member x.CloseAllFoldsInSelection (visualSpan : VisualSpan) =
        let span = visualSpan.LineRange.Extent
        _foldManager.CloseAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete 'count' characters after the cursor on the current line.  Caret should 
    /// remain at it's original position 
    member x.DeleteCharacterAtCaret count register =

        // Check for the case where the caret is past the end of the line.  Can happen
        // when 've=onemore'
        if x.CaretPoint.Position < x.CaretLine.End.Position then
            let endPoint = SnapshotLineUtil.GetOffsetOrEnd x.CaretLine (x.CaretColumn + count)
            let span = SnapshotSpan(x.CaretPoint, endPoint)

            // Use a transaction so we can guarantee the caret is in the correct
            // position on undo / redo
            x.EditWithUndoTransaciton "DeleteChar" (fun () -> 
                let position = x.CaretPoint.Position
                let snapshot = _textBuffer.Delete(span.Span)

                // Need to respect the virtual edit setting here as we could have 
                // deleted the last character on the line
                let point = SnapshotPoint(snapshot, position)
                _operations.MoveCaretToPointAndCheckVirtualSpace point)

            // Put the deleted text into the specified register
            let value = RegisterValue.String (StringData.OfSpan span, OperationKind.CharacterWise)
            _registerMap.SetRegisterValue register RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete 'count' characters before the cursor on the current line.  Caret should be
    /// positioned at the begining of the span for undo / redo
    member x.DeleteCharacterBeforeCaret count register = 

        let startPoint = 
            let position = x.CaretPoint.Position - count
            if position < x.CaretLine.Start.Position then x.CaretLine.Start else SnapshotPoint(x.CurrentSnapshot, position)
        let span = SnapshotSpan(startPoint, x.CaretPoint)

        // Use a transaction so we can guarantee the caret is in the correct position.  We 
        // need to position the caret to the start of the span before the transaction to 
        // ensure it appears there during an undo
        TextViewUtil.MoveCaretToPoint _textView startPoint
        x.EditWithUndoTransaciton "DeleteChar" (fun () ->
            let snapshot = _textBuffer.Delete(span.Span)
            TextViewUtil.MoveCaretToPosition _textView startPoint.Position)

        // Put the deleted text into the specified register once the delete completes
        let value = RegisterValue.String (StringData.OfSpan span, OperationKind.CharacterWise)
        _registerMap.SetRegisterValue register RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete a fold from the selection
    member x.DeleteFoldInSelection (visualSpan : VisualSpan) =
        let range = visualSpan.LineRange
        let offset = range.StartLineNumber
        for i = 0 to range.Count - 1 do
            let line = SnapshotUtil.GetLine x.CurrentSnapshot (offset + 1)
            _foldManager.DeleteFold line.Start
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete a fold under the caret
    member x.DeleteFoldUnderCaret () = 
        _foldManager.DeleteFold x.CaretPoint
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete a fold from the selection
    member x.DeleteAllFoldInSelection (visualSpan : VisualSpan) =
        let span = visualSpan.LineRange.Extent
        _foldManager.DeleteAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete all folds under the caret
    member x.DeleteAllFoldsUnderCaret () =
        let span = SnapshotSpan(x.CaretPoint, 0)
        _foldManager.DeleteAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete all of the folds in the ITextBuffer
    member x.DeleteAllFoldsInBuffer () =
        let extent = SnapshotUtil.GetExtent x.CurrentSnapshot
        _foldManager.DeleteAllFolds extent
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete the selected text from the buffer and put it into the specified 
    /// register. 
    member x.DeleteLineSelection register (visualSpan : VisualSpan) =

        // For each of the 3 cases the caret should begin at the start of the 
        // VisualSpan during undo so move the caret now. 
        TextViewUtil.MoveCaretToPoint _textView visualSpan.Start

        // Start a transaction so we can manipulate the caret position during 
        // an undo / redo
        let editSpan = 
            x.EditWithUndoTransaciton "Delete" (fun () -> 
    
                use edit = _textBuffer.CreateEdit()
                let editSpan = 
                    match visualSpan with
                    | VisualSpan.Character span ->
                        // Just extend the SnapshotSpan to the encompasing SnapshotLineRange 
                        let range = SnapshotLineRangeUtil.CreateForSpan span
                        let span = range.ExtentIncludingLineBreak
                        edit.Delete(span.Span) |> ignore
                        EditSpan.Single span
                    | VisualSpan.Line range ->
                        // Easiest case.  It's just the range
                        edit.Delete(range.ExtentIncludingLineBreak.Span) |> ignore
                        EditSpan.Single range.ExtentIncludingLineBreak
                    | VisualSpan.Block col -> 
                        col
                        |> Seq.iter (fun span -> 
                            // Delete from the start of the span until the end of the containing
                            // line
                            let span = 
                                let line = SnapshotPointUtil.GetContainingLine span.Start
                                SnapshotSpan(span.Start, line.End)
                            edit.Delete(span.Span) |> ignore)
                        EditSpan.Block col
    
                edit.Apply() |> ignore
    
                // Now position the cursor back at the start of the VisualSpan
                //
                // Possible for a block mode to deletion to cause the start to now be in the line 
                // break so we need to acount for the 'virtualedit' setting
                let point = SnapshotPoint(x.CurrentSnapshot, visualSpan.Start.Position)
                _operations.MoveCaretToPointAndCheckVirtualSpace point

                editSpan)

        let value = RegisterValue.String (StringData.OfEditSpan editSpan, OperationKind.LineWise)
        _registerMap.SetRegisterValue register RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Delete the highlighted text from the buffer and put it into the specified 
    /// register.  The caret should be positioned at the begining of the text for
    /// undo / redo
    member x.DeleteSelection register (visualSpan : VisualSpan) = 
        let startPoint = visualSpan.Start

        // Use a transaction to guarantee caret position.  Caret should be at the start
        // during undo and redo so move it before the edit
        TextViewUtil.MoveCaretToPoint _textView startPoint
        x.EditWithUndoTransaciton "DeleteSelection" (fun () ->
            use edit = _textBuffer.CreateEdit()
            visualSpan.Spans |> Seq.iter (fun span -> 

                // If the last included point in the SnapshotSpan is inside the line break
                // portion of a line then extend the SnapshotSpan to encompass the full
                // line break
                let span =
                    match SnapshotSpanUtil.GetLastIncludedPoint span with
                    | None -> 
                        // Don't need to special case a 0 length span as it won't actually
                        // cause any change in the ITextBuffer
                        span
                    | Some last ->
                        if SnapshotPointUtil.IsInsideLineBreak last then
                            let line = SnapshotPointUtil.GetContainingLine last
                            SnapshotSpan(span.Start, line.EndIncludingLineBreak)
                        else
                            span

                edit.Delete(span.Span) |> ignore)
            let snapshot = edit.Apply()
            TextViewUtil.MoveCaretToPosition _textView startPoint.Position)

        let operationKind = visualSpan.OperationKind
        let value = RegisterValue.String (StringData.OfEditSpan visualSpan.EditSpan, operationKind)
        _registerMap.SetRegisterValue register RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Delete count lines from the cursor.  The caret should be positioned at the start
    /// of the first line for both undo / redo
    member x.DeleteLines count register = 

        let span, includesLastLine =
            // The span should be calculated using the visual snapshot if available.  Binding 
            // it as 'x' here will help prevent us from accidentally mixing the visual and text
            // snapshot values
            let x = TextViewUtil.GetVisualSnapshotDataOrEdit _textView
            let line = x.CaretLine
            if line.LineNumber = SnapshotUtil.GetLastLineNumber x.CurrentSnapshot && x.CurrentSnapshot.LineCount > 1 then
                // The last line is an unfortunate special case here as it does not have a line break.  Hence 
                // in order to delete the line we must delete the line break at the end of the preceeding line.  
                //
                // This cannot be normalized by always deleting the line break from the previous line because
                // it would still break for the first line.  This is an unfortunate special case we must 
                // deal with
                let above = SnapshotUtil.GetLine x.CurrentSnapshot (line.LineNumber - 1)
                let span = SnapshotSpan(above.End, line.EndIncludingLineBreak)
                span, true
            else 
                // Simpler case.  Get the line range and delete
                let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
                range.ExtentIncludingLineBreak, false

        // Make sure to map the SnapshotSpan back into the text / edit buffer
        match BufferGraphUtil.MapSpanDownToSingle _bufferGraph span x.CurrentSnapshot with
        | None ->
            // If we couldn't map back down raise an error
            _statusUtil.OnError Resources.Internal_ErrorMappingToVisual
        | Some span ->

            // When calculating the text to put into the register we must add in a trailing new line
            // if we were dealing with the last line.  The last line won't include a line break but 
            // we require that line wise values end in breaks for consistency
            let stringData = 
                if includesLastLine then
                    (span.GetText()) + System.Environment.NewLine |> EditUtil.RemoveBeginingNewLine |> StringData.Simple
                else
                    StringData.OfSpan span

            // Use a transaction to properly position the caret for undo / redo.  We want it in the same
            // place for undo / redo so move it before the transaction
            TextViewUtil.MoveCaretToPoint _textView span.Start
            x.EditWithUndoTransaciton "DeleteLines" (fun() -> 
                let snapshot = _textBuffer.Delete(span.Span)
                TextViewUtil.MoveCaretToPoint _textView (SnapshotPoint(snapshot, span.Start.Position)))

            // Now update the register after the delete completes
            let value = RegisterValue.String (stringData, OperationKind.LineWise)
            _registerMap.SetRegisterValue register RegisterOperation.Delete value
    
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete the specified motion of text
    member x.DeleteMotion register (result : MotionResult) = 

        // The d{motion} command has an exception listed which is visible by typing ':help d' in 
        // gVim.  In summary, if the motion is characterwise, begins and ends on different
        // lines and the start is preceeding by only whitespace and the end is followed
        // only by whitespace then it becomes a linewise motion for those lines.  However experimentation
        // shows that this does not appear to be the case.  For example type the following out 
        // where ^ is the caret
        //
        //  ^abc
        //   def
        //
        // Then try 'd/    '.  It will not delete the final line even though this meets all of
        // the requirements.  Choosing to ignore this exception for now until I can find
        // a better example

        // Caret should be placed at the start of the motion for both undo / redo so place it 
        // before starting the transaction
        let span = result.Span
        TextViewUtil.MoveCaretToPoint _textView span.Start
        x.EditWithUndoTransaciton "Delete" (fun () ->
            _textBuffer.Delete(span.Span) |> ignore

            // Get the point on the current ITextSnapshot
            let point = SnapshotPoint(x.CurrentSnapshot, span.Start.Position)
            _operations.MoveCaretToPointAndCheckVirtualSpace point)

        // Update the register with the result so long as something was actually deleted
        // from the buffer
        if not span.IsEmpty then
            let value = RegisterValue.String (StringData.OfSpan span, result.OperationKind)
            _registerMap.SetRegisterValue register RegisterOperation.Delete value

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete from the cursor to the end of the line and then 'count - 1' more lines into
    /// the buffer.  This is the implementation of the 'D' command
    member x.DeleteTillEndOfLine count register =

        let caretPosition = x.CaretPoint.Position

        // The caret is already at the start of the Span and it needs to be after the 
        // delete so wrap it in an undo transaction
        x.EditWithUndoTransaciton "Delete" (fun () -> 
            x.DeleteTillEndOfLineCore count register

            // Move the caret back to the original position in the ITextBuffer.
            let point = SnapshotPoint(x.CurrentSnapshot, caretPosition)
            _operations.MoveCaretToPointAndCheckVirtualSpace point)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Delete from the caret to the end of the line and 'count - 1' more lines
    member x.DeleteTillEndOfLineCore count register = 
        let span = 
            if count = 1 then
                // Just deleting till the end of the caret line
                SnapshotSpan(x.CaretPoint, x.CaretLine.End)
            else
                // Grab a SnapshotLineRange for the 'count - 1' lines and combine in with
                // the caret start to get the span
                let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
                SnapshotSpan(x.CaretPoint, range.End)

        _textBuffer.Delete(span.Span) |> ignore

        // Delete is complete so update the register.  Strangely enough this is a character wise
        // operation even though it involves line deletion
        let value = RegisterValue.String (StringData.OfSpan span, OperationKind.CharacterWise)
        _registerMap.SetRegisterValue register RegisterOperation.Delete value

    /// Run the specified action with a wrapped undo transaction.  This is often necessary when
    /// an edit command manipulates the caret
    member x.EditWithUndoTransaciton<'T> (name : string) (action : unit -> 'T) : 'T = 
        _undoRedoOperations.EditWithUndoTransaction name action

    /// Used for the several commands which make an edit here and need the edit to be linked
    /// with the next insert mode change.  
    member x.EditWithLinkedChange name action =
        let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()

        try
            x.EditWithUndoTransaciton name action
        with
            | _ ->
                // If the above throws we can't leave the transaction open else it will
                // break undo / redo in the ITextBuffer.  Close it here and
                // re-raise the exception
                transaction.Dispose()
                reraise()

        let arg = ModeArgument.InsertWithTransaction transaction
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, arg))

    /// Used for commands which need to operate on the visual buffer and produce a SnapshotSpan
    /// to be mapped back to the text / edit buffer
    member x.EditWithVisualSnapshot action = 
        let snapshotData = TextViewUtil.GetVisualSnapshotDataOrEdit _textView
        let span = action snapshotData
        BufferGraphUtil.MapSpanDownToSingle _bufferGraph span x.CurrentSnapshot

    /// Close a fold under the caret for 'count' lines
    member x.FoldLines count =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        _foldManager.CreateFold range
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Create a fold for the given MotionResult
    member x.FoldMotion (result : MotionResult) =
        _foldManager.CreateFold result.LineRange

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Fold the specified selection 
    member x.FoldSelection (visualSpan : VisualSpan) = 
        _foldManager.CreateFold visualSpan.LineRange

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Format the 'count' lines in the buffer
    member x.FormatLines count =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        _operations.FormatLines range
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Format the selected lines
    member x.FormatLinesVisual (visualSpan: VisualSpan) =

        // Use a transaction so the formats occur as a single operation
        x.EditWithUndoTransaciton "Format" (fun () ->
            visualSpan.Spans
            |> Seq.map SnapshotLineRangeUtil.CreateForSpan
            |> Seq.iter _operations.FormatLines)

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Format the lines in the Motion 
    member x.FormatMotion (result : MotionResult) = 
        _operations.FormatLines result.LineRange
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Go to the definition of the word under the caret
    member x.GoToDefinition () =
        match _operations.GoToDefinition() with
        | Result.Succeeded -> ()
        | Result.Failed(msg) -> _statusUtil.OnError msg

        CommandResult.Completed ModeSwitch.NoSwitch

    /// GoTo the file name under the cursor and possibly use a new window
    member x.GoToFileUnderCaret useNewWindow =
        if useNewWindow then _operations.GoToFileInNewWindow()
        else _operations.GoToFile()

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Go to the global declaration of the word under the caret
    member x.GoToGlobalDeclaration () =
        _operations.GoToGlobalDeclaration()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Go to the local declaration of the word under the caret
    member x.GoToLocalDeclaration () =
        _operations.GoToLocalDeclaration()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Go to the next tab in the specified direction
    member x.GoToNextTab path countOption = 
        match path with
        | Path.Forward ->
            match countOption with
            | Some count -> _operations.GoToTab count
            | None -> _operations.GoToNextTab path 1
        | Path.Backward ->
            let count = countOption |> OptionUtil.getOrDefault 1
            _operations.GoToNextTab Path.Backward count

        CommandResult.Completed ModeSwitch.NoSwitch

    /// GoTo the ITextView in the specified direction
    member x.GoToView direction = 
        match direction with
        | Direction.Up -> _vimHost.MoveViewUp _textView
        | Direction.Down -> _vimHost.MoveViewDown _textView
        | Direction.Left -> _vimHost.MoveViewLeft _textView
        | Direction.Right -> _vimHost.MoveViewRight _textView

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Join 'count' lines in the buffer
    member x.JoinLines kind count = 

        // An oddity of the join command is that the count 1 and 2 have the same effect.  Easiest
        // to treat both values as 2 since the math works out for all other values above 2
        let count = if count = 1 then 2 else count

        match SnapshotLineRangeUtil.CreateForLineAndCount x.CaretLine count with
        | None -> 
            // If the count exceeds the length of the buffer then the operation should not 
            // complete and a beep should be issued
            _operations.Beep()
        | Some range -> 
            // The caret should be positioned one after the second to last line in the 
            // join.  It should have it's original position during an undo so don't
            // move the caret until we're inside the transaction
            x.EditWithUndoTransaciton "Join" (fun () -> 
                _operations.Join range kind
                x.MoveCaretFollowingJoin range)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Join the selection of lines in the buffer
    member x.JoinSelection kind (visualSpan : VisualSpan) = 
        let range = SnapshotLineRangeUtil.CreateForSpan visualSpan.EditSpan.OverarchingSpan 

        // Extend the range to at least 2 lines if possible
        let range = 
            if range.Count = 1 && range.EndLineNumber = SnapshotUtil.GetLastLineNumber range.Snapshot then
                // Can't extend
                range
            elif range.Count = 1 then
                // Extend it 1 line
                SnapshotLineRange(range.Snapshot, range.StartLineNumber, 2)
            else
                // Already at least 2 lines
                range

        if range.Count = 1 then
            // Can't join a single line
            _operations.Beep()

            CommandResult.Completed ModeSwitch.NoSwitch
        else 
            // The caret before the join should be positioned at the start of the VisualSpan
            TextViewUtil.MoveCaretToPoint _textView visualSpan.Start
            x.EditWithUndoTransaciton "Join" (fun () -> 
                _operations.Join range kind
                x.MoveCaretFollowingJoin range)

            CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Switch to insert mode after the caret 
    member x.InsertAfterCaret count = 
        let point = x.CaretPoint
        if SnapshotPointUtil.IsInsideLineBreak point then 
            ()
        elif SnapshotPointUtil.IsEndPoint point then 
            ()
        else 
            let point = point.Add(1)
            TextViewUtil.MoveCaretToPoint _textView point

        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count))

    /// Switch to Insert mode with the specified count
    member x.InsertBeforeCaret count =
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count))

    /// Switch to insert mode at the end of the line
    member x.InsertAtEndOfLine count =
        TextViewUtil.MoveCaretToPoint _textView x.CaretLine.End

        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count))

    /// Begin insert mode on the first non-blank character of the line.  Pass the count onto
    /// insert mode so it can duplicate the input
    member x.InsertAtFirstNonBlank count =
        let point = 
            x.CaretLine
            |> SnapshotLineUtil.GetPoints Path.Forward
            |> Seq.skipWhile SnapshotPointUtil.IsWhiteSpace
            |> SeqUtil.tryHeadOnly
            |> OptionUtil.getOrDefault x.CaretLine.End
        TextViewUtil.MoveCaretToPoint _textView point

        let switch = ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count)
        CommandResult.Completed switch

    /// Switch to insert mode at the start of the line
    member x.InsertAtStartOfLine count =
        TextViewUtil.MoveCaretToPoint _textView x.CaretLine.Start

        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCount count))

    /// Insert a line above the current caret line and begin insert mode at the start of that
    /// line
    member x.InsertLineAbove count = 
        let savedCaretLine = x.CaretLine

        // REPEAT TODO: Need to file a bug to get the caret position correct here for redo
        _undoRedoOperations.EditWithUndoTransaction "InsertLineAbove" (fun() -> 
            let line = x.CaretLine
            _textBuffer.Replace(new Span(line.Start.Position,0), System.Environment.NewLine) |> ignore)

        // Position the caret for the edit
        let line = SnapshotUtil.GetLine x.CurrentSnapshot savedCaretLine.LineNumber
        x.MoveCaretToNewLineIndent savedCaretLine line

        let switch = ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCountAndNewLine count)
        CommandResult.Completed switch

    /// Insert a line below the current caret line and begin insert mode at the start of that 
    /// line
    member x.InsertLineBelow count = 

        // The caret position here odd.  The caret during undo / redo should be in the original
        // caret position.  However the edit needs to occur with the caret indented on the newly
        // created line.  So there are actually 3 caret positions to consider here
        //
        //  1. Before Edit (Undo)
        //  2. After the Edit but in the Transaction (Redo)
        //  3. For the eventual user edit

        let savedCaretPoint = x.CaretPoint
        let savedCaretLine = x.CaretLine
        _undoRedoOperations.EditWithUndoTransaction  "InsertLineBelow" (fun () -> 
            let span = new SnapshotSpan(savedCaretLine.EndIncludingLineBreak, 0)
            _textBuffer.Replace(span.Span, System.Environment.NewLine) |> ignore

            TextViewUtil.MoveCaretToPosition _textView savedCaretPoint.Position)

        let newLine = SnapshotUtil.GetLine x.CurrentSnapshot (savedCaretLine.LineNumber + 1)
        x.MoveCaretToNewLineIndent savedCaretLine newLine

        let switch = ModeSwitch.SwitchModeWithArgument (ModeKind.Insert, ModeArgument.InsertWithCountAndNewLine count)
        CommandResult.Completed switch

    /// Jump to the next tag in the tag list
    member x.JumpToNewerPosition count = 
        if not (_jumpList.MoveNewer count) then
            _operations.Beep()
        else
            x.JumpToTagCore ()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Jump to the previous tag in the tag list
    member x.JumpToOlderPosition count = 
        // If this is the first jump which starts a traversal then we should reset the head
        // to this point and begin the traversal
        let atStart = (_jumpList.CurrentIndex |> OptionUtil.getOrDefault 0) = 0
        if atStart then
            _jumpList.Add x.CaretPoint

        if not (_jumpList.MoveOlder count) then
            _operations.Beep()
        else
            x.JumpToTagCore ()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Jump to the specified mark
    member x.JumpToMark c =
        match _operations.JumpToMark c _markMap with
        | Result.Failed msg ->
            _statusUtil.OnError msg
            CommandResult.Error
        | Result.Succeeded ->
            CommandResult.Completed ModeSwitch.NoSwitch

    /// Jumps to the specified 
    member x.JumpToTagCore () =
        match _jumpList.Current with
        | None -> _operations.Beep()
        | Some point -> _operations.MoveCaretToPointAndEnsureVisible point

    /// Move the caret to start of a line which is deleted.  Needs to preserve the original 
    /// indent if 'autoindent' is set.
    ///
    /// Be wary of using this function.  It has the implicit contract that the Start position
    /// of the line is still valid.  
    member x.MoveCaretToDeletedLineStart (deletedLine : ITextSnapshotLine) =
        Contract.Requires (deletedLine.Start.Position <= x.CurrentSnapshot.Length)

        if _localSettings.AutoIndent then
            // Caret needs to be positioned at the indentation point of the previous line.  Don't
            // create actual whitespace, put the caret instead into virtual space
            let column = 
                deletedLine.Start
                |> SnapshotPointUtil.GetContainingLine
                |> SnapshotLineUtil.GetIndent
                |> SnapshotPointUtil.GetColumn
            if column = 0 then 
                TextViewUtil.MoveCaretToPosition _textView deletedLine.Start.Position
            else
                let point = SnapshotUtil.GetPoint x.CurrentSnapshot deletedLine.Start.Position
                let virtualPoint = VirtualSnapshotPoint(point, column)
                TextViewUtil.MoveCaretToVirtualPoint _textView virtualPoint
        else
            // Put the caret at column 0
            TextViewUtil.MoveCaretToPosition _textView deletedLine.Start.Position

    /// Move the caret to the indentation point applicable for a new line in the ITextBuffer
    member x.MoveCaretToNewLineIndent oldLine (newLine : ITextSnapshotLine) =
        let doVimIndent() = 
            if _localSettings.AutoIndent then
                let indent = oldLine |> SnapshotLineUtil.GetIndent |> SnapshotPointUtil.GetColumn
                let point = new VirtualSnapshotPoint(newLine, indent)
                TextViewUtil.MoveCaretToVirtualPoint _textView point |> ignore 
            else
                TextViewUtil.MoveCaretToPoint _textView newLine.Start |> ignore

        if _localSettings.GlobalSettings.UseEditorIndent then
            let indent = _smartIndentationService.GetDesiredIndentation(_textView, newLine)
            if indent.HasValue then 
                let point = new VirtualSnapshotPoint(newLine, indent.Value)
                TextViewUtil.MoveCaretToVirtualPoint _textView point |> ignore
            else
               doVimIndent()
        else 
            doVimIndent()

    /// The Join commands (Visual and Normal) have identical cursor positioning behavior and 
    /// it's non-trivial so it's factored out to a function here.  In short the caret should be
    /// positioned 1 position after the last character in the second to last line of the join
    /// The caret should be positioned one after the second to last line in the 
    /// join.  It should have it's original position during an undo so don't
    /// move the caret until we're inside the transaction
    member x.MoveCaretFollowingJoin (range : SnapshotLineRange) =
        let point = 
            let number = range.StartLineNumber + range.Count - 2
            let line = SnapshotUtil.GetLine range.Snapshot number
            line |> SnapshotLineUtil.GetLastIncludedPoint |> OptionUtil.getOrDefault line.Start
        match TrackingPointUtil.GetPointInSnapshot point PointTrackingMode.Positive x.CurrentSnapshot with
        | None -> 
            ()
        | Some point -> 
            let point = SnapshotPointUtil.AddOneOrCurrent point
            TextViewUtil.MoveCaretToPoint _textView point

    /// Move the caret to the result of the motion
    member x.MoveCaretToMotion motion count = 
        let argument = { MotionContext = MotionContext.Movement; OperatorCount = None; MotionCount = count}
        match _motionUtil.GetMotion motion argument with
        | None -> 
            // If the motion couldn't be gotten then just beep
            _operations.Beep()
            CommandResult.Error
        | Some result -> 

            let point = x.CaretPoint
            _operations.MoveCaretToMotionResult result

            // Beep if the motion doesn't actually move the caret.  This is currently done to 
            // satisfy 'l' and 'h' at the end and start of lines respectively.  It may not be 
            // needed for every empty motion but so far I can't find a reason why not
            if point = x.CaretPoint then 
                _operations.Beep()
                CommandResult.Error
            else
                CommandResult.Completed ModeSwitch.NoSwitch

    /// Open a fold in visual mode.  In Visual Mode a single fold level is opened for every
    /// line in the selection
    member x.OpenFoldInSelection (visualSpan : VisualSpan) = 
        let range = visualSpan.LineRange
        let offset = range.StartLineNumber
        for i = 0 to range.Count - 1 do
            let line = SnapshotUtil.GetLine x.CurrentSnapshot (offset + 1)
            _foldManager.OpenFold line.Start 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Open 'count' folds under the caret
    member x.OpenFoldUnderCaret count = 
        _foldManager.OpenFold x.CaretPoint count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Open all of the folds under the caret 
    member x.OpenAllFoldsUnderCaret () =
        let span = SnapshotSpan(x.CaretPoint, 1)
        _foldManager.OpenAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Open all folds under the caret in visual mode
    member x.OpenAllFoldsInSelection (visualSpan : VisualSpan) = 
        let span = visualSpan.LineRange.ExtentIncludingLineBreak
        _foldManager.OpenAllFolds span
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Run the Ping command
    member x.Ping (pingData : PingData) data = 
        pingData.Function data

    /// Put the contents of the specified register after the cursor.  Used for the
    /// 'p' and 'gp' command in normal mode
    member x.PutAfterCaret (register : Register) count moveCaretAfterText =
        x.PutAfterCaretCore (register.RegisterValue) count moveCaretAfterText 
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Core put after function used by many of the put after operations
    member x.PutAfterCaretCore (registerValue : RegisterValue) count moveCaretAfterText =
        let stringData = registerValue.StringData.ApplyCount count
        let point = 
            match registerValue.OperationKind with
            | OperationKind.CharacterWise -> 
                if x.CaretLine.Length = 0 then 
                    x.CaretLine.Start
                else
                    SnapshotPointUtil.AddOneOrCurrent x.CaretPoint
            | OperationKind.LineWise -> 
                x.CaretLine.EndIncludingLineBreak

        x.PutCore point stringData registerValue.OperationKind moveCaretAfterText

    /// Put the contents of the register into the buffer after the cursor and respect
    /// the indent of the current line.  Used for the ']p' command
    member x.PutAfterCaretWithIndent (register : Register) count = 
        let registerValue = x.CalculateIdentStringData register.RegisterValue
        x.PutAfterCaretCore registerValue count false
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Put the contents of the specified register before the cursor.  Used for the
    /// 'P' and 'gP' commands in normal mode
    member x.PutBeforeCaret (register : Register) count moveCaretAfterText =
        x.PutBeforeCaretCore register.RegisterValue count moveCaretAfterText
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Put the contents of the specified register before the caret and respect the
    /// indent of the current line.  Used for the '[p' and family commands
    member x.PutBeforeCaretWithIndent (register : Register) count =
        let registerValue = x.CalculateIdentStringData register.RegisterValue
        x.PutBeforeCaretCore registerValue count false
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Core put function used by many of the put before operations
    member x.PutBeforeCaretCore (registerValue : RegisterValue) count moveCaretAfterText =
        let stringData = registerValue.StringData.ApplyCount count
        let point = 
            match registerValue.OperationKind with
            | OperationKind.CharacterWise -> x.CaretPoint
            | OperationKind.LineWise -> x.CaretLine.Start

        x.PutCore point stringData registerValue.OperationKind moveCaretAfterText

    /// Put the contents of the specified register after the cursor.  Used for the
    /// normal 'p', 'gp', 'P', 'gP', ']p' and '[p' commands.  For linewise put operations 
    /// the point must be at the start of a line
    member x.PutCore point stringData operationKind moveCaretAfterText =

        // Save the point incase this is a linewise insertion and we need to
        // move after the inserted lines
        let oldPoint = point

        // The caret should be positioned at the current position in undo so don't move
        // it before the transaction.
        x.EditWithUndoTransaciton "Put" (fun () -> 

            _operations.Put point stringData operationKind

            // Edit is complete.  Position the caret against the updated text.  First though
            // get the original insertion point in the new ITextSnapshot
            let point = SnapshotUtil.GetPoint x.CurrentSnapshot point.Position
            match operationKind with
            | OperationKind.CharacterWise -> 

                let point = 
                    match stringData with
                    | StringData.Simple text ->
                        if EditUtil.HasNewLine text && not moveCaretAfterText then 
                            // For multi-line operations which do not specify to move the caret after
                            // the text we instead put the caret at the first character of the new 
                            // text
                            point
                        else
                            // For characterwise we just increment the length of the first string inserted
                            // and possibily one more if moving after
                            let offset = stringData.FirstString.Length - 1
                            let offset = max 0 offset
                            let point = SnapshotPointUtil.Add offset point
                            if moveCaretAfterText then SnapshotPointUtil.AddOneOrCurrent point else point
                    | StringData.Block col -> 
                        if moveCaretAfterText then
                            // Needs to be positioned after the last item in the collection
                            let line = 
                                let number = oldPoint |> SnapshotPointUtil.GetContainingLine |> SnapshotLineUtil.GetLineNumber
                                let number = number + (col.Count - 1)
                                SnapshotUtil.GetLine x.CurrentSnapshot number
                            let offset = (SnapshotPointUtil.GetColumn point) + col.Head.Length
                            SnapshotPointUtil.Add offset line.Start
                        else
                            // Position at the original insertion point
                            SnapshotUtil.GetPoint x.CurrentSnapshot oldPoint.Position

                _operations.MoveCaretToPointAndCheckVirtualSpace point
            | OperationKind.LineWise ->

                // Get the line on which we will be positioning the caret
                let line = 
                    if moveCaretAfterText then
                        // Move to the first line after the insertion.  Can be calculated with a line
                        // count offset
                        let offset = x.CurrentSnapshot.LineCount - oldPoint.Snapshot.LineCount
                        let number = oldPoint |> SnapshotPointUtil.GetContainingLine |> SnapshotLineUtil.GetLineNumber
                        SnapshotUtil.GetLine x.CurrentSnapshot (number + offset)
                    else
                        // The caret should be moved to the first line of the inserted text.
                        let number = 
                            let oldLineNumber = oldPoint |> SnapshotPointUtil.GetContainingLine |> SnapshotLineUtil.GetLineNumber
                            if SnapshotPointUtil.IsStartOfLine oldPoint then 
                                oldLineNumber
                            else
                                // Anything other than the start of the line will cause the Put to 
                                // occur one line below and we need to account for that
                                oldLineNumber + 1
                        SnapshotUtil.GetLine x.CurrentSnapshot number

                // Get the indent point of the line.  That's what the caret needs to be moved to
                let point = SnapshotLineUtil.GetIndent line
                _operations.MoveCaretToPointAndCheckVirtualSpace point)

    /// Put the contents of the specified register over the selection.  This is used for all
    /// visual mode put commands. 
    member x.PutOverSelection (register : Register) count moveCaretAfterText visualSpan = 

        // Build up the common variables
        let stringData = register.StringData.ApplyCount count
        let operationKind = register.OperationKind

        let deletedSpan, operationKind = 
            match visualSpan with
            | VisualSpan.Character span ->

                // Cursor needs to be at the start of the span during undo and at the end
                // of the pasted span after redo so move to the start before the undo transaction
                TextViewUtil.MoveCaretToPoint _textView span.Start
                x.EditWithUndoTransaciton "Put" (fun () ->
    
                    // Delete the span and move the caret back to the start of the 
                    // span in the new ITextSnapshot
                    _textBuffer.Delete(span.Span) |> ignore
                    TextViewUtil.MoveCaretToPosition _textView span.Start.Position

                    // Now do a standard put operation at the original start point in the current
                    // ITextSnapshot
                    let point = SnapshotUtil.GetPoint x.CurrentSnapshot span.Start.Position
                    x.PutCore point stringData operationKind moveCaretAfterText

                    EditSpan.Single span, OperationKind.CharacterWise)
            | VisualSpan.Line range ->

                // Cursor needs to be positioned at the start of the range for both undo so
                // move the caret now 
                TextViewUtil.MoveCaretToPoint _textView range.Start
                x.EditWithUndoTransaciton "Put" (fun () ->

                    // When putting over a linewise selection the put needs to be done
                    // in a linewise fashion.  This means in certain cases we have to adjust
                    // the StringData to have proper newline semantics
                    let stringData = 
                        match stringData with
                        | StringData.Simple str ->
                            let str = if EditUtil.EndsWithNewLine str then str else str + (EditUtil.NewLine _options)
                            StringData.Simple str
                        | StringData.Block _ -> 
                            stringData
                    let operationKind = OperationKind.LineWise

                    // Delete the span and move the caret back to the start
                    _textBuffer.Delete(range.ExtentIncludingLineBreak.Span) |> ignore
                    TextViewUtil.MoveCaretToPosition _textView range.Start.Position

                    // Now do a standard put operation at the start of the SnapshotLineRange
                    // in the current ITextSnapshot
                    let point = SnapshotUtil.GetPoint x.CurrentSnapshot range.Start.Position
                    x.PutCore point stringData operationKind moveCaretAfterText

                    EditSpan.Single range.ExtentIncludingLineBreak, OperationKind.LineWise)

            | VisualSpan.Block col ->

                // Cursor needs to be positioned at the start of the range for undo so
                // move the caret now
                let span = col.Head
                TextViewUtil.MoveCaretToPoint _textView span.Start
                x.EditWithUndoTransaciton "Put" (fun () ->

                    // Delete all of the items in the collection
                    use edit = _textBuffer.CreateEdit()
                    col |> Seq.iter (fun span -> edit.Delete(span.Span) |> ignore)
                    edit.Apply() |> ignore

                    // Now do a standard put operation.  The point of the put varies a bit 
                    // based on whether we're doing a linewise or characterwise insert
                    let point = 
                        match operationKind with
                        | OperationKind.CharacterWise -> 
                            // Put occurs at the start of the original span
                            SnapshotUtil.GetPoint x.CurrentSnapshot span.Start.Position
                        | OperationKind.LineWise -> 
                            // Put occurs on the line after the last edit
                            let lastSpan = col |> SeqUtil.last
                            let number = lastSpan.Start |> SnapshotPointUtil.GetContainingLine |> SnapshotLineUtil.GetLineNumber
                            SnapshotUtil.GetLine x.CurrentSnapshot number |> SnapshotLineUtil.GetEndIncludingLineBreak
                    x.PutCore point stringData operationKind moveCaretAfterText

                    EditSpan.Block col, OperationKind.CharacterWise)

        // Update the unnamed register with the deleted text
        let value = RegisterValue.String (StringData.OfEditSpan deletedSpan, operationKind)
        let unnamedRegister = _registerMap.GetRegister RegisterName.Unnamed
        _registerMap.SetRegisterValue unnamedRegister RegisterOperation.Delete value 

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Start a macro recording
    member x.RecordMacroStart c = 
        let isAppend, c = 
            if CharUtil.IsUpper c && CharUtil.IsLetter c then
                true, CharUtil.ToLower c
            else
                false, c

        // Restrict the register to the valid ones for macros
        let name = 
            if CharUtil.IsLetter c then
                NamedRegister.OfChar c |> Option.map RegisterName.Named
            elif CharUtil.IsDigit c then
                NumberedRegister.OfChar c |> Option.map RegisterName.Numbered
            elif c = '"' then
                RegisterName.Unnamed |> Some
            else 
                None

        match name with 
        | None ->
            // Beep on an invalid macro register
            _operations.Beep()
        | Some name ->
            let register = _registerMap.GetRegister name
            _buffer.Vim.MacroRecorder.StartRecording register isAppend

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Stop a macro recording
    member x.RecordMacroStop () =
        _buffer.Vim.MacroRecorder.StopRecording()
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Undo count operations in the ITextBuffer
    member x.Redo count = 
        _operations.Redo count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Repeat the last executed command against the current buffer
    member x.RepeatLastCommand (repeatData : CommandData) = 

        // Function to actually repeat the last change 
        let rec repeat (command : StoredCommand) (repeatData : CommandData option) = 

            // Calculate the new CommandData based on the original and repeat CommandData 
            // values.  The repeat CommandData will be None in nested command repeats 
            // for linked commands which means the original should just be used
            let getCommandData (original : CommandData) = 
                match repeatData with
                | None -> 
                    original
                | Some repeatData -> 
                    match repeatData.Count with
                    | Some count -> { original with Count = repeatData.Count }
                    | None -> original

            match command with
            | StoredCommand.NormalCommand (command, data, _) ->
                let data = getCommandData data
                x.RunNormalCommand command data
            | StoredCommand.VisualCommand (command, data, storedVisualSpan, _) -> 
                let data = getCommandData data
                let visualSpan = x.CalculateVisualSpan storedVisualSpan
                x.RunVisualCommand command data visualSpan
            | StoredCommand.TextChangeCommand change ->
                // Calculate the count of the repeat
                let count = 
                    match repeatData with
                    | Some repeatData -> repeatData.CountOrDefault
                    | None -> 1
                x.RepeatTextChange change count
            | StoredCommand.LinkedCommand (command1, command2) -> 

                // Running linked commands will throw away the ModeSwitch value.  This can contain
                // an open IUndoTransaction.  This must be completed here or it will break undo in the 
                // ITextBuffer
                let maybeCloseTransaction modeSwitch = 
                    match modeSwitch with
                    | ModeSwitch.SwitchModeWithArgument (_, argument) ->
                        match argument with
                        | ModeArgument.None -> ()
                        | ModeArgument.FromVisual -> ()
                        | ModeArgument.InsertWithCount _ -> ()
                        | ModeArgument.InsertWithCountAndNewLine _ -> ()
                        | ModeArgument.InsertWithTransaction transaction -> transaction.Complete()
                        | ModeArgument.OneTimeCommand _ -> ()
                        | ModeArgument.Substitute _ -> ()
                    | _ -> ()

                // Run the commands in sequence.  Only continue onto the second if the first 
                // command succeeds.  We do want any actions performed in the linked commands
                // to remain linked so do this inside of an edit transaction
                x.EditWithUndoTransaciton "LinkedCommand" (fun () ->
                    match repeat command1 repeatData with
                    | CommandResult.Error -> 
                        CommandResult.Error
                    | CommandResult.Completed modeSwitch ->
                        maybeCloseTransaction modeSwitch
                        repeat command2 None)

        if _inRepeatLastChange then
            _statusUtil.OnError Resources.NormalMode_RecursiveRepeatDetected
            CommandResult.Error 
        else
            try
                _inRepeatLastChange <- true
                match _vimData.LastCommand with
                | None -> 
                    _operations.Beep()
                    CommandResult.Completed ModeSwitch.NoSwitch
                | Some command ->
                    repeat command (Some repeatData)
            finally
                _inRepeatLastChange <- false

    /// Repeat the last subsitute command.  
    member x.RepeatLastSubstitute useSameFlags = 
        match _vimData.LastSubstituteData with
        | None ->
            _operations.Beep()
        | Some data ->
            let range = SnapshotLineRangeUtil.CreateForLine x.CaretLine
            let flags = 
                if useSameFlags then
                    data.Flags
                else
                    SubstituteFlags.None
            _operations.Substitute data.SearchPattern data.Substitute range flags

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Repeat the TextChange value 'count' times in the ITextBuffer
    member x.RepeatTextChange textChange count =

        x.EditWithUndoTransaciton "Repeat Text Change" (fun () ->

            // First apply the TextChange to the buffer then we will position the caret
            _operations.ApplyTextChange textChange false count

            // Next we need to do the final positioning of the caret.  While replaying 
            // a series of edits we put the caret in a very particular place in order to 
            // make the edits line up.  Once the edits are complete we need to reposition 
            // the caret one item to the left.  This is to simulate the leaving of insert 
            // mode and the caret moving left
            let point = 
                match SnapshotPointUtil.TryGetPreviousPointOnLine x.CaretPoint 1 with
                | None -> x.CaretPoint
                | Some point -> point
            TextViewUtil.MoveCaretToPoint _textView point)

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Replace the text at the caret via replace mode
    member x.ReplaceAtCaret count =
        let switch = ModeSwitch.SwitchModeWithArgument (ModeKind.Replace, ModeArgument.InsertWithCount count)
        CommandResult.Completed switch

    /// Replace the char under the cursor with the specified character
    member x.ReplaceChar keyInput count = 

        let succeeded = 
            let point = x.CaretPoint
            if (point.Position + count) > point.GetContainingLine().End.Position then
                // If the replace operation exceeds the line length then the operation
                // can't succeed
                _operations.Beep()
                false
            else
                // Do the replace in an undo transaction since we are explicitly positioning
                // the caret
                x.EditWithUndoTransaciton "ReplaceChar" (fun () -> 

                    let replaceText = 
                        if keyInput = KeyInputUtil.EnterKey then EditUtil.NewLine _options
                        else new System.String(keyInput.Char, count)
                    let span = new Span(point.Position, count)
                    let snapshot = _textView.TextBuffer.Replace(span, replaceText) 

                    // The caret should move to the end of the replace operation which is 
                    // 'count - 1' characters from the original position 
                    let point = SnapshotPoint(snapshot, point.Position + (count - 1))

                    _textView.Caret.MoveTo(point) |> ignore)
                true

        // If the replace failed then we should beep the console
        if not succeeded then
            _operations.Beep()

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Replace the char under the cursor in visual mode.
    member x.ReplaceSelection keyInput (visualSpan : VisualSpan) = 

        let replaceText = 
            if keyInput = KeyInputUtil.EnterKey then EditUtil.NewLine _options
            else System.String(keyInput.Char, 1)

        // First step is we want to update the selection.  A replace char operation
        // in visual mode should position the caret on the first character and clear
        // the selection (both before and after).
        //
        // The caret can be anywhere at the start of the operation so move it to the
        // first point before even beginning the edit transaction
        _textView.Selection.Clear()
        let points = 
            visualSpan.Spans
            |> Seq.map (SnapshotSpanUtil.GetPoints Path.Forward)
            |> Seq.concat
        let editPoint = 
            match points |> SeqUtil.tryHeadOnly with
            | Some point -> point
            | None -> x.CaretPoint
        TextViewUtil.MoveCaretToPoint _textView editPoint

        x.EditWithUndoTransaciton "ReplaceChar" (fun () -> 
            use edit = _textBuffer.CreateEdit()
            points |> Seq.iter (fun point -> edit.Replace((Span(point.Position, 1)), replaceText) |> ignore)
            let snapshot = edit.Apply()

            // Reposition the caret at the start of the edit
            let editPoint = SnapshotPoint(snapshot, editPoint.Position)
            TextViewUtil.MoveCaretToPoint _textView editPoint)

        CommandResult.Completed (ModeSwitch.SwitchMode ModeKind.Normal)

    /// Run the specified Command
    member x.RunCommand command = 
        match command with
        | Command.NormalCommand (command, data) -> x.RunNormalCommand command data
        | Command.VisualCommand (command, data, visualSpan) -> x.RunVisualCommand command data visualSpan

    /// Run the Macro which is present in the specified char
    member x.RunMacro registerName count = 

        // If the '@' is used then we are doing a run last macro run
        let registerName = 
            if registerName = '@' then _vimData.LastMacroRun |> OptionUtil.getOrDefault registerName
            else registerName

        let name = 
            // TODO:  Need to handle, = and .
            if CharUtil.IsDigit registerName then
                NumberedRegister.OfChar registerName |> Option.map RegisterName.Numbered
            elif registerName = '*' then
                SelectionAndDropRegister.Register_Star |> RegisterName.SelectionAndDrop |> Some
            else
                let registerName = CharUtil.ToLower registerName
                NamedRegister.OfChar registerName |> Option.map RegisterName.Named

        match name with
        | None ->
            _operations.Beep()
        | Some name ->
            let register = _registerMap.GetRegister name
            let list = register.RegisterValue.KeyInputList

            // The macro should be executed as a single action and the macro can execute in
            // several ITextBuffer instances (consider if the macros executes a 'gt' and keeps
            // editing).  We need to have proper transactions for every ITextBuffer this macro
            // runs in
            //
            // Using .Net dictionary because we have to map by ITextBuffer which doesn't have
            // the comparison constraint
            let map = System.Collections.Generic.Dictionary<ITextBuffer, IUndoTransaction>();

            try 

                // Actually run the macro by replaying the key strokes one at a time.  Returns 
                // false if the macro should be stopped due to a failed command
                let runMacro () =
                    let rec inner list = 
                        match list with 
                        | [] -> 
                            // No more input so we are finished
                            true
                        | keyInput :: tail -> 

                            match _vim.FocusedBuffer with
                            | None -> 
                                // Nothing to do if we don't have an ITextBuffer with focus
                                false
                            | Some buffer -> 
                                // Make sure we have an IUndoTransaction open in the ITextBuffer
                                if not (map.ContainsKey(buffer.TextBuffer)) then
                                    let transaction = _undoRedoOperations.CreateUndoTransaction "Macro Run"
                                    map.Add(buffer.TextBuffer, transaction)
                                    transaction.AddBeforeTextBufferChangePrimitive()
            
                                // Actually run the KeyInput.  If processing the KeyInput value results
                                // in an error then we should stop processing the macro
                                match buffer.Process keyInput with
                                | ProcessResult.Handled _ -> inner tail
                                | ProcessResult.NotHandled -> false
                                | ProcessResult.Error -> false

                    inner list

                // Run the macro count times. 
                let go = ref true
                for i = 1 to count do
                    if go.Value then
                        go := runMacro()

                // Close out all of the transactions
                for transaction in map.Values do
                    transaction.AddAfterTextBufferChangePrimitive()
                    transaction.Complete()

            finally
                // Make sure to dispose the transactions in a finally block.  Leaving them open
                // completely breaks undo in the ITextBuffer
                map.Values |> Seq.iter (fun transaction -> transaction.Dispose())

            _vimData.LastMacroRun <- Some registerName

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Run a NormalCommand against the buffer
    member x.RunNormalCommand command (data : CommandData) =
        let register = _registerMap.GetRegister data.RegisterNameOrDefault
        let count = data.CountOrDefault
        match command with
        | NormalCommand.ChangeMotion motion -> x.RunWithMotion motion (x.ChangeMotion register)
        | NormalCommand.ChangeCaseCaretLine kind -> x.ChangeCaseCaretLine kind
        | NormalCommand.ChangeCaseCaretPoint kind -> x.ChangeCaseCaretPoint kind count
        | NormalCommand.ChangeCaseMotion (kind, motion) -> x.RunWithMotion motion (x.ChangeCaseMotion kind)
        | NormalCommand.ChangeLines -> x.ChangeLines count register
        | NormalCommand.ChangeTillEndOfLine -> x.ChangeTillEndOfLine count register
        | NormalCommand.CloseAllFoldsUnderCaret -> x.CloseAllFoldsUnderCaret ()
        | NormalCommand.CloseFoldUnderCaret -> x.CloseFoldUnderCaret count
        | NormalCommand.DeleteAllFoldsInBuffer -> x.DeleteAllFoldsInBuffer ()
        | NormalCommand.DeleteAllFoldsUnderCaret -> x.DeleteAllFoldsUnderCaret ()
        | NormalCommand.DeleteCharacterAtCaret -> x.DeleteCharacterAtCaret count register
        | NormalCommand.DeleteCharacterBeforeCaret -> x.DeleteCharacterBeforeCaret count register
        | NormalCommand.DeleteFoldUnderCaret -> x.DeleteFoldUnderCaret ()
        | NormalCommand.DeleteLines -> x.DeleteLines count register
        | NormalCommand.DeleteMotion motion -> x.RunWithMotion motion (x.DeleteMotion register)
        | NormalCommand.DeleteTillEndOfLine -> x.DeleteTillEndOfLine count register
        | NormalCommand.FoldLines -> x.FoldLines data.CountOrDefault
        | NormalCommand.FoldMotion motion -> x.RunWithMotion motion x.FoldMotion
        | NormalCommand.FormatLines -> x.FormatLines count
        | NormalCommand.FormatMotion motion -> x.RunWithMotion motion x.FormatMotion 
        | NormalCommand.GoToDefinition -> x.GoToDefinition ()
        | NormalCommand.GoToFileUnderCaret useNewWindow -> x.GoToFileUnderCaret useNewWindow
        | NormalCommand.GoToGlobalDeclaration -> x.GoToGlobalDeclaration ()
        | NormalCommand.GoToLocalDeclaration -> x.GoToLocalDeclaration ()
        | NormalCommand.GoToNextTab path -> x.GoToNextTab path data.Count
        | NormalCommand.GoToView direction -> x.GoToView direction
        | NormalCommand.InsertAfterCaret -> x.InsertAfterCaret count
        | NormalCommand.InsertBeforeCaret -> x.InsertBeforeCaret count
        | NormalCommand.InsertAtEndOfLine -> x.InsertAtEndOfLine count
        | NormalCommand.InsertAtFirstNonBlank -> x.InsertAtFirstNonBlank count
        | NormalCommand.InsertAtStartOfLine -> x.InsertAtStartOfLine count
        | NormalCommand.InsertLineAbove -> x.InsertLineAbove count
        | NormalCommand.InsertLineBelow -> x.InsertLineBelow count
        | NormalCommand.JoinLines kind -> x.JoinLines kind count
        | NormalCommand.JumpToMark c -> x.JumpToMark c
        | NormalCommand.JumpToOlderPosition -> x.JumpToOlderPosition count
        | NormalCommand.JumpToNewerPosition -> x.JumpToNewerPosition count
        | NormalCommand.MoveCaretToMotion motion -> x.MoveCaretToMotion motion data.Count
        | NormalCommand.OpenAllFoldsUnderCaret -> x.OpenAllFoldsUnderCaret ()
        | NormalCommand.OpenFoldUnderCaret -> x.OpenFoldUnderCaret data.CountOrDefault
        | NormalCommand.Ping pingData -> x.Ping pingData data
        | NormalCommand.PutAfterCaret moveCaretAfterText -> x.PutAfterCaret register count moveCaretAfterText
        | NormalCommand.PutAfterCaretWithIndent -> x.PutAfterCaretWithIndent register count
        | NormalCommand.PutBeforeCaret moveCaretBeforeText -> x.PutBeforeCaret register count moveCaretBeforeText
        | NormalCommand.PutBeforeCaretWithIndent -> x.PutBeforeCaretWithIndent register count
        | NormalCommand.RecordMacroStart c -> x.RecordMacroStart c
        | NormalCommand.RecordMacroStop -> x.RecordMacroStop ()
        | NormalCommand.Redo -> x.Redo count
        | NormalCommand.RepeatLastCommand -> x.RepeatLastCommand data
        | NormalCommand.RepeatLastSubstitute useSameFlags -> x.RepeatLastSubstitute useSameFlags
        | NormalCommand.ReplaceAtCaret -> x.ReplaceAtCaret count
        | NormalCommand.ReplaceChar keyInput -> x.ReplaceChar keyInput data.CountOrDefault
        | NormalCommand.RunMacro registerName -> x.RunMacro registerName data.CountOrDefault
        | NormalCommand.SetMarkToCaret c -> x.SetMarkToCaret c
        | NormalCommand.ScrollLines (direction, useScrollOption) -> x.ScrollLines direction useScrollOption data.Count
        | NormalCommand.ScrollPages direction -> x.ScrollPages direction data.CountOrDefault
        | NormalCommand.ScrollCaretLineToTop keepCaretColumn -> x.ScrollCaretLineToTop keepCaretColumn
        | NormalCommand.ScrollCaretLineToMiddle keepCaretColumn -> x.ScrollCaretLineToMiddle keepCaretColumn
        | NormalCommand.ScrollCaretLineToBottom keepCaretColumn -> x.ScrollCaretLineToBottom keepCaretColumn
        | NormalCommand.SubstituteCharacterAtCaret -> x.SubstituteCharacterAtCaret count register
        | NormalCommand.ShiftLinesLeft -> x.ShiftLinesLeft count
        | NormalCommand.ShiftLinesRight -> x.ShiftLinesRight count
        | NormalCommand.ShiftMotionLinesLeft motion -> x.RunWithMotion motion x.ShiftMotionLinesLeft
        | NormalCommand.ShiftMotionLinesRight motion -> x.RunWithMotion motion x.ShiftMotionLinesRight
        | NormalCommand.SplitViewHorizontally -> x.SplitViewHorizontally()
        | NormalCommand.SplitViewVertically -> x.SplitViewVertically()
        | NormalCommand.SwitchMode (modeKind, modeArgument) -> x.SwitchMode modeKind modeArgument
        | NormalCommand.Undo -> x.Undo count
        | NormalCommand.WriteBufferAndQuit -> x.WriteBufferAndQuit ()
        | NormalCommand.Yank motion -> x.RunWithMotion motion (x.YankMotion register)
        | NormalCommand.YankLines -> x.YankLines count register

    /// Run a VisualCommand against the buffer
    member x.RunVisualCommand command (data : CommandData) (visualSpan : VisualSpan) = 

        // Clear the selection before actually running any Visual Commands.  Selection is one 
        // of the items which is preserved along with caret position when we use an edit transaction
        // with the change primitives (EditWithUndoTransaction).  We don't want the selection to 
        // reappear during an undo hence clear it now so it's gone.
        _textView.Selection.Clear()

        let register = _registerMap.GetRegister data.RegisterNameOrDefault
        let count = data.CountOrDefault
        match command with
        | VisualCommand.ChangeCase kind -> x.ChangeCaseVisual kind visualSpan
        | VisualCommand.ChangeSelection -> x.ChangeSelection register visualSpan
        | VisualCommand.CloseAllFoldsInSelection -> x.CloseAllFoldsInSelection visualSpan
        | VisualCommand.CloseFoldInSelection -> x.CloseFoldInSelection visualSpan
        | VisualCommand.ChangeLineSelection specialCaseBlock -> x.ChangeLineSelection register visualSpan specialCaseBlock
        | VisualCommand.DeleteAllFoldsInSelection -> x.DeleteAllFoldInSelection visualSpan
        | VisualCommand.DeleteFoldInSelection -> x.DeleteFoldInSelection visualSpan
        | VisualCommand.DeleteSelection -> x.DeleteSelection register visualSpan
        | VisualCommand.DeleteLineSelection -> x.DeleteLineSelection register visualSpan
        | VisualCommand.FormatLines -> x.FormatLinesVisual visualSpan
        | VisualCommand.FoldSelection -> x.FoldSelection visualSpan
        | VisualCommand.JoinSelection kind -> x.JoinSelection kind visualSpan
        | VisualCommand.OpenFoldInSelection -> x.OpenFoldInSelection visualSpan
        | VisualCommand.OpenAllFoldsInSelection -> x.OpenAllFoldsInSelection visualSpan
        | VisualCommand.PutOverSelection moveCaretAfterText -> x.PutOverSelection register count moveCaretAfterText visualSpan 
        | VisualCommand.ReplaceSelection keyInput -> x.ReplaceSelection keyInput visualSpan
        | VisualCommand.ShiftLinesLeft -> x.ShiftLinesLeftVisual count visualSpan
        | VisualCommand.ShiftLinesRight -> x.ShiftLinesRightVisual count visualSpan
        | VisualCommand.YankLineSelection -> x.YankLineSelection register visualSpan
        | VisualCommand.YankSelection -> x.YankSelection register visualSpan

    /// Get the MotionResult value for the provided MotionData and pass it
    /// if found to the provided function
    member x.RunWithMotion (motion : MotionData) func = 
        match _motionUtil.GetMotion motion.Motion motion.MotionArgument with
        | None -> 
            _operations.Beep()
            CommandResult.Error
        | Some data ->
            func data

    /// Process the m[a-z] command
    member x.SetMarkToCaret c = 
        let caretPoint = TextViewUtil.GetCaretPoint _textView
        match _operations.SetMark caretPoint c _markMap with
        | Result.Failed msg ->
            _operations.Beep()
            _statusUtil.OnError msg
            CommandResult.Error
        | Result.Succeeded ->
            CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll the lines 'count' pages in the specified direction
    ///
    /// TODO: Should support the 'scroll' option here.  It should be the used value 
    /// when a count is not specified
    member x.ScrollLines direction useScrollOption countOption =
        let count = 
            match countOption with
            | None -> TextViewUtil.GetVisibleLineCount _textView / 2
            | Some count -> count
        let count = if count <= 0 then 1 else count

        let lineNumber = 
            match direction with
            | ScrollDirection.Up -> 
                if x.CaretLine.LineNumber = 0 then 
                    _operations.Beep()
                    None
                elif x.CaretLine.LineNumber < count then
                    0 |> Some
                else
                    x.CaretLine.LineNumber - count |> Some
            | ScrollDirection.Down ->
                if x.CaretLine.LineNumber = SnapshotUtil.GetLastLineNumber x.CurrentSnapshot then
                    _operations.Beep()
                    None
                else
                    x.CaretLine.LineNumber + count |> Some
            | _ -> 
                None

        match lineNumber |> Option.map (fun number -> SnapshotUtil.GetLineOrLast x.CurrentSnapshot number) with
        | None ->
            ()
        | Some line -> 
            let point = 
                let column = SnapshotPointUtil.GetColumn x.CaretPoint
                if column < line.Length then
                    line.Start.Add(column)
                else
                    line.Start
            TextViewUtil.MoveCaretToPoint _textView point
            _operations.EnsureCaretOnScreen()

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll a page in the specified direction
    member x.ScrollPages direction count = 
        let doScroll () = 
            match direction with
            | ScrollDirection.Up -> _editorOperations.PageUp(false)
            | ScrollDirection.Down -> _editorOperations.PageDown(false)
            | _ -> _operations.Beep()

        for i = 1 to count do
            doScroll()

        // The editor PageUp and PageDown don't actually move the caret.  Manually move it 
        // here
        let line = 
            match direction with 
            | ScrollDirection.Up -> _textView.TextViewLines.FirstVisibleLine
            | ScrollDirection.Down -> _textView.TextViewLines.LastVisibleLine
            | _ -> _textView.TextViewLines.FirstVisibleLine
        _textView.Caret.MoveTo(line) |> ignore

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll the line containing the caret to the top of the ITextView.  
    member x.ScrollCaretLineToTop keepCaretColumn = 
        _operations.EditorOperations.ScrollLineTop()
        if not keepCaretColumn then
            _operations.EditorOperations.MoveToStartOfLineAfterWhiteSpace(false)
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll the line containing the caret to the middle of the ITextView.  
    member x.ScrollCaretLineToMiddle keepCaretColumn = 
        _operations.EditorOperations.ScrollLineCenter()
        if not keepCaretColumn then
            _operations.EditorOperations.MoveToStartOfLineAfterWhiteSpace(false)
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Scroll the line containing the caret to the bottom of the ITextView.  
    member x.ScrollCaretLineToBottom keepCaretColumn = 
        _operations.EditorOperations.ScrollLineBottom()
        if not keepCaretColumn then
            _operations.EditorOperations.MoveToStartOfLineAfterWhiteSpace(false)
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Shift the given line range left by the specified value.  The caret will be 
    /// placed at the first character on the first line of the shifted text
    member x.ShiftLinesLeftCore range multiplier =

        // Use a transaction so the caret will be properly moved for undo / redo
        x.EditWithUndoTransaciton "ShiftLeft" (fun () ->
            _operations.ShiftLineRangeLeft range multiplier

            // Now move the caret to the first non-whitespace character on the first
            // line 
            let line = SnapshotUtil.GetLine x.CurrentSnapshot range.StartLineNumber
            let point = 
                match SnapshotLineUtil.GetFirstNonBlank line with 
                | None -> SnapshotLineUtil.GetLastIncludedPoint line |> OptionUtil.getOrDefault line.Start
                | Some point -> point
            TextViewUtil.MoveCaretToPoint _textView point)

    /// Shift the given line range left by the specified value.  The caret will be 
    /// placed at the first character on the first line of the shifted text
    member x.ShiftLinesRightCore range multiplier =

        // Use a transaction so the caret will be properly moved for undo / redo
        x.EditWithUndoTransaciton "ShiftRight" (fun () ->
            _operations.ShiftLineRangeRight range multiplier

            // Now move the caret to the first non-whitespace character on the first
            // line 
            let line = SnapshotUtil.GetLine x.CurrentSnapshot range.StartLineNumber
            let point = 
                match SnapshotLineUtil.GetFirstNonBlank line with 
                | None -> SnapshotLineUtil.GetLastIncludedPoint line |> OptionUtil.getOrDefault line.Start
                | Some point -> point
            TextViewUtil.MoveCaretToPoint _textView point)

    /// Shift 'count' lines to the left 
    member x.ShiftLinesLeft count =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        x.ShiftLinesLeftCore range 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Shift 'motion' lines to the left by 'count' shiftwidth.
    member x.ShiftLinesLeftVisual count visualSpan = 

        // Both Character and Line spans operate like most shifts
        match visualSpan with
        | VisualSpan.Character span ->
            let range = SnapshotLineRangeUtil.CreateForSpan span
            x.ShiftLinesLeftCore range count
        | VisualSpan.Line range ->
            x.ShiftLinesLeftCore range count
        | VisualSpan.Block col ->
            // Shifting a block span is trickier because it doesn't shift at column
            // 0 but rather shifts at the start column of every span.  It also treats
            // the caret much more different by keeping it at the start of the first
            // span vs. the start of the shift
            let targetCaretPosition = visualSpan.Start.Position

            // Use a transaction to preserve the caret.  But move the caret first since
            // it needs to be undone to this location
            TextViewUtil.MoveCaretToPosition _textView targetCaretPosition
            x.EditWithUndoTransaciton "ShiftLeft" (fun () -> 
                _operations.ShiftLineBlockRight col count
                TextViewUtil.MoveCaretToPosition _textView targetCaretPosition)

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Shift 'count' lines to the right 
    member x.ShiftLinesRight count =
        let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
        x.ShiftLinesRightCore range 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Shift 'motion' lines to the right by 'count' shiftwidth
    member x.ShiftLinesRightVisual count visualSpan = 

        // Both Character and Line spans operate like most shifts
        match visualSpan with
        | VisualSpan.Character span ->
            let range = SnapshotLineRangeUtil.CreateForSpan span
            x.ShiftLinesRightCore range count
        | VisualSpan.Line range ->
            x.ShiftLinesRightCore range count
        | VisualSpan.Block col ->
            // Shifting a block span is trickier because it doesn't shift at column
            // 0 but rather shifts at the start column of every span.  It also treats
            // the caret much more different by keeping it at the start of the first
            // span vs. the start of the shift
            let targetCaretPosition = visualSpan.Start.Position 

            // Use a transaction to preserve the caret.  But move the caret first since
            // it needs to be undone to this location
            TextViewUtil.MoveCaretToPosition _textView targetCaretPosition
            x.EditWithUndoTransaciton "ShiftLeft" (fun () -> 
                _operations.ShiftLineBlockRight col count

                TextViewUtil.MoveCaretToPosition _textView targetCaretPosition)

        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Shift 'motion' lines to the left
    member x.ShiftMotionLinesLeft (result : MotionResult) = 
        x.ShiftLinesLeftCore result.LineRange 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Shift 'motion' lines to the right
    member x.ShiftMotionLinesRight (result : MotionResult) = 
        x.ShiftLinesRightCore result.LineRange 1
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Split the view horizontally
    member x.SplitViewHorizontally () = 
        match _vimHost.SplitViewHorizontally _textView with
        | HostResult.Success -> ()
        | HostResult.Error _ -> _operations.Beep()

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Split the view vertically
    member x.SplitViewVertically () =
        match _vimHost.SplitViewVertically _textView with
        | HostResult.Success -> ()
        | HostResult.Error _ -> _operations.Beep()

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Substitute 'count' characters at the cursor on the current line.  Very similar to
    /// DeleteCharacterAtCaret.  Main exception is the behavior when the caret is on
    /// or after the last character in the line
    /// should be after the span for Substitute even if 've='.  
    member x.SubstituteCharacterAtCaret count register =

        x.EditWithLinkedChange "Substitute" (fun () ->
            if x.CaretPoint.Position >= x.CaretLine.End.Position then
                // When we are past the end of the line just move the caret
                // to the end of the line and complete the command.  Nothing should be deleted
                TextViewUtil.MoveCaretToPoint _textView x.CaretLine.End
            else
                let endPoint = SnapshotLineUtil.GetOffsetOrEnd x.CaretLine (x.CaretColumn + count)
                let span = SnapshotSpan(x.CaretPoint, endPoint)
    
                // Use a transaction so we can guarantee the caret is in the correct
                // position on undo / redo
                x.EditWithUndoTransaciton "DeleteChar" (fun () -> 
                    let position = x.CaretPoint.Position
                    let snapshot = _textBuffer.Delete(span.Span)
                    TextViewUtil.MoveCaretToPoint _textView (SnapshotPoint(snapshot, position)))
    
                // Put the deleted text into the specified register
                let value = RegisterValue.String (StringData.OfSpan span, OperationKind.CharacterWise)
                _registerMap.SetRegisterValue register RegisterOperation.Delete value)

    /// Switch to the given mode
    member x.SwitchMode modeKind modeArgument = 
        CommandResult.Completed (ModeSwitch.SwitchModeWithArgument (modeKind, modeArgument))

    /// Undo count operations in the ITextBuffer
    member x.Undo count = 
        _operations.Undo count
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Write out the ITextBuffer and quit
    member x.WriteBufferAndQuit () =
        let result = 
            if _vimHost.IsDirty _textBuffer then
                _vimHost.Save _textBuffer
            else
                true

        if result then
            _vimHost.Close _textView false
            CommandResult.Completed ModeSwitch.NoSwitch
        else
            CommandResult.Error

    /// Yank the specified lines into the specified register.  This command should operate
    /// against the visual buffer if possible.  Yanking a line which contains the fold should
    /// yank the entire fold
    member x.YankLines count register = 
        let span = x.EditWithVisualSnapshot (fun x -> 

            // Get the line range in the snapshot data
            let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount x.CaretLine count
            range.ExtentIncludingLineBreak)

        match span with
        | None ->
            // If we couldn't map back down raise an error
            _statusUtil.OnError Resources.Internal_ErrorMappingToVisual
        | Some span ->

            let data = StringData.OfSpan span
            let value = RegisterValue.String (data, OperationKind.LineWise)
            _registerMap.SetRegisterValue register RegisterOperation.Yank value

        CommandResult.Completed ModeSwitch.NoSwitch

    /// Yank the contents of the motion into the specified register
    member x.YankMotion register (result: MotionResult) = 
        let value = RegisterValue.String (StringData.OfSpan result.Span, result.OperationKind)
        _registerMap.SetRegisterValue register RegisterOperation.Yank value
        CommandResult.Completed ModeSwitch.NoSwitch

    /// Yank the lines in the specified selection
    member x.YankLineSelection register (visualSpan : VisualSpan) = 
        let editSpan, operationKind = 
            match visualSpan with 
            | VisualSpan.Character span ->
                // Extend the character selection to the full lines
                let range = SnapshotLineRangeUtil.CreateForSpan span
                EditSpan.Single range.ExtentIncludingLineBreak, OperationKind.LineWise
            | VisualSpan.Line _ ->
                // Simple case, just use the visual span as is
                visualSpan.EditSpan, OperationKind.LineWise
            | VisualSpan.Block _ ->
                // Odd case.  Don't treat any different than a normal yank
                visualSpan.EditSpan, visualSpan.OperationKind

        let data = StringData.OfEditSpan editSpan
        let value = RegisterValue.String (data, operationKind)
        _registerMap.SetRegisterValue register RegisterOperation.Yank value
        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    /// Yank the selection into the specified register
    member x.YankSelection register (visualSpan : VisualSpan) = 
        let data = StringData.OfEditSpan visualSpan.EditSpan
        let value = RegisterValue.String (data, visualSpan.OperationKind)
        _registerMap.SetRegisterValue register RegisterOperation.Yank value
        CommandResult.Completed ModeSwitch.SwitchPreviousMode

    interface ICommandUtil with
        member x.RunNormalCommand command data = x.RunNormalCommand command data
        member x.RunVisualCommand command data visualSpan = x.RunVisualCommand command data visualSpan 
        member x.RunCommand command = x.RunCommand command

