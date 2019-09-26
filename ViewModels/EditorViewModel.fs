﻿namespace FVim

open common
open log
open ui
open wcwidth
open neovim.def

open ReactiveUI
open Avalonia
open Avalonia.Input
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform
open Avalonia.Threading
open Avalonia.Skia
open FSharp.Control.Reactive

open System
open System.Collections.ObjectModel
open Avalonia.Controls
open System.Reactive.Disposables
open SkiaSharp
open System.Runtime.InteropServices

type EditorViewModel(GridId: int, ?parent: EditorViewModel, ?_gridsize: GridSize, ?_glyphsize: Size, ?_measuredsize: Size, ?_fontsize: float, ?_gridscale: float,
                     ?_hldefs: HighlightAttr[], ?_modedefs: ModeInfo[], ?_guifont: string, ?_guifontwide: string, ?_cursormode: int, ?_anchorX: float, ?_anchorY: float) as this =
    inherit ViewModelBase(_anchorX, _anchorY, _measuredsize)

    let trace fmt = trace (sprintf "editorvm #%d" GridId) fmt

    let m_cursor_vm              = new CursorViewModel(_cursormode)
    let m_popupmenu_vm           = new PopupMenuViewModel()
    let m_child_grids            = ObservableCollection<EditorViewModel>()
    let m_resize_ev              = Event<IGridUI>()
    let m_input_ev               = Event<int*InputEvent>()
    let m_hlchange_ev            = Event<unit>()
    let m_tick_ev                = Event<unit>()

    let mutable m_busy           = false

    let mutable m_mouse_en       = true
    let mutable m_mouse_pressed  = MouseButton.None
    let mutable m_mouse_pos      = 0,0

    let mutable m_default_fg     = Colors.White
    let mutable m_default_bg     = Colors.Black
    let mutable m_default_sp     = Colors.Red

    let mutable m_hi_defs        = match _hldefs with
                                     | None -> Array.create<HighlightAttr> 256 HighlightAttr.Default
                                     | Some arr -> arr.Clone() :?> HighlightAttr[]
    let mutable m_mode_defs      = match _modedefs with
                                     | None -> Array.empty<ModeInfo>
                                     | Some arr -> arr.Clone() :?> ModeInfo[]
    let mutable m_semhl          = Map.empty

    let mutable m_guifont        = _d DefaultFont     _guifont
    let mutable m_guifontwide    = _d DefaultFontWide _guifontwide
    let mutable m_fontsize       = _d 16.0 _fontsize
    let mutable m_glyphsize      = _d (Size(10.0, 10.0)) _glyphsize

    let mutable m_gridsize       = _d { rows = 10; cols= 10 } _gridsize
    let mutable m_gridscale      = _d 1.0 _gridscale
    let mutable m_gridfullscreen = false
    let mutable m_gridbuffer     = Array2D.create m_gridsize.rows m_gridsize.cols GridBufferCell.empty
    let mutable m_griddirty      = GridRegion()

    let mutable m_fb_h           = 10.0
    let mutable m_fb_w           = 10.0

    let toggleFullScreen(gridid: int) =
        if gridid = GridId then
            trace "ToggleFullScreen"
            this.Fullscreen <- not this.Fullscreen

    let getPos (p: Point) =
        int(p.X / m_glyphsize.Width), int(p.Y / m_glyphsize.Height)

    let markDirty = m_griddirty.Union

    let markAllDirty () =
        m_griddirty.Clear()
        m_griddirty.Union{ row = 0; col = 0; height = m_gridsize.rows; width = m_gridsize.cols }

    let flush() = 
        trace "flush."
        this.RenderTick <- this.RenderTick + 1

    let fontConfig() =
        m_fontsize <- max m_fontsize 1.0
        // It turns out the space " " advances farest...
        // So we measure it as the width.
        let s, w, h = MeasureText(" ", m_guifont, m_guifontwide, m_fontsize, m_gridscale)
        m_glyphsize <- Size(w, h)
        m_fontsize <- s

        trace "fontConfig: guifont=%s guifontwide=%s size=%A" m_guifont m_guifontwide m_glyphsize
        // sync font to cursor vm
        this.cursorConfig()
        // sync font to popupmenu vm
        m_popupmenu_vm.SetFont(m_guifont, m_fontsize)
        markAllDirty()
        m_resize_ev.Trigger(this)

    let setHighlight x =
        if m_hi_defs.Length < x.id + 1 then
            Array.Resize(&m_hi_defs, x.id + 100)
        m_hi_defs.[x.id] <- x
        if x.id = 0 then
            m_default_fg <- x.rgb_attr.foreground.Value
            m_default_bg <- x.rgb_attr.background.Value
            m_default_sp <- x.rgb_attr.special.Value
            this.RaisePropertyChanged("BackgroundBrush")
        m_hlchange_ev.Trigger()

    let setSemanticHighlightGroups grp =
        m_semhl <- grp
        // update popupmenu color
        let [ nfg, nbg, _, _
              sfg, sbg, _, _
              scfg, scbg, _, _
              _, bbg, _, _ ] = 
            [
                SemanticHighlightGroup.Pmenu
                SemanticHighlightGroup.PmenuSel
                SemanticHighlightGroup.PmenuSbar
                SemanticHighlightGroup.VertSplit
            ] 
            |> List.map (m_semhl.TryFind >> Option.defaultValue 1 >> this.GetDrawAttrs)

        m_popupmenu_vm.SetColors(nfg, nbg, sfg, sbg, scfg, scbg, bbg)

    let setDefaultColors fg bg sp = 

        let bg = 
            if fg = bg && bg = sp then GetReverseColor bg
            else bg

        setHighlight {
            id = 0
            info = [||]
            cterm_attr = RgbAttr.Empty
            rgb_attr = { 
                foreground = Some fg
                background = Some bg
                special = Some sp
                reverse = false
                italic = false
                bold = false
                underline = false
                undercurl = false
            }
        }
        trace "setDefaultColors: %A %A %A" fg bg sp

    let clearBuffer () =
        m_gridbuffer <- Array2D.create m_gridsize.rows m_gridsize.cols GridBufferCell.empty
        // notify buffer update and size change
        let size: Point = this.GetPoint m_gridsize.rows m_gridsize.cols
        m_fb_w <- size.X
        m_fb_h <- size.Y
        this.RaisePropertyChanged("BufferHeight")
        this.RaisePropertyChanged("BufferWidth")

    let putBuffer (line: GridLine) =
        let         row  = line.row
        let mutable col  = line.col_start
        let mutable hlid = 0
        let mutable rep = 1
        for cell in line.cells do
            hlid <- Option.defaultValue hlid cell.hl_id
            rep  <- Option.defaultValue 1 cell.repeat
            for _i = 1 to rep do
                m_gridbuffer.[row, col].hlid <- hlid
                m_gridbuffer.[row, col].text <- cell.text
                col <- col + 1
        let dirty = { row = row; col = line.col_start; height = 1; width = col - line.col_start } 
        // if the buffer under cursor is updated, also notify the cursor view model
        if row = m_cursor_vm.row && line.col_start <= m_cursor_vm.col && m_cursor_vm.col < col
        then this.cursorConfig()
        //trace "redraw" "putBuffer: writing to %A" dirty
        // italic font artifacts I: remainders after scrolling and redrawing the dirty part
        // workaround: extend the dirty region one cell further towards the end

        // italic font artifacts II: when inserting on an italic line, later glyphs cover earlier with the background.
        // workaround: if italic, extend the dirty region towards the beginning, until not italic

        // italic font artifacts III: block cursor may not have italic style. 
        // how to fix this? curious about how the original GVim handles this situation.

        // ligature artifacts I: ligatures do not build as characters are laid down.
        // workaround: like italic, case II.

        // apply workaround I:
        let dirty = {dirty with width = min (dirty.width + 1) m_gridsize.cols }
        // apply workaround II:
        col  <- dirty.col - 1
        let mutable italic = true
        let mutable ligature = true
        while col > 0 && (italic || ligature) do
            hlid <- m_gridbuffer.[row, col].hlid
            col <- col - 1
            ligature <- isProgrammingSymbol m_gridbuffer.[row, col].text
            italic <- m_hi_defs.[hlid].rgb_attr.italic 
        let dirty = {dirty with width = dirty.width + (dirty.col - col); col = col }
        

        markDirty dirty

    let setModeInfo (cs_en: bool) (info: ModeInfo[]) =
        m_mode_defs <- info
        this.setCursorEnabled cs_en

    let cursorGoto id row col =
        m_cursor_vm.ingrid <- (id = GridId)
        m_cursor_vm.row <- row
        m_cursor_vm.col <- col
        this.cursorConfig()

    let changeMode (name: string) (index: int) = 
        m_cursor_vm.modeidx <- index
        this.cursorConfig()

    let bell (visual: bool) =
        // TODO
        trace "neovim: bell: %A" visual
        ()

    let setBusy (v: bool) =
        trace "neovim: busy: %A" v
        m_busy <- v
        this.setCursorEnabled <| not v
        //if v then this.Cursor <- Cursor(StandardCursorType.Wait)
        //else this.Cursor <- Cursor(StandardCursorType.Arrow)
        //flush()

    let scrollBuffer (top: int) (bot: int) (left: int) (right: int) (rows: int) (cols: int) =
        //  !NOTE top-bot are the bounds of the SCROLL-REGION, not SRC or DST.
        //        scrollBuffer first specifies the SR, and offsets SRC/DST according
        //        to the following rules:
        //
        //    If `rows` is bigger than 0, move a rectangle in the SR up, this can
        //    happen while scrolling down.
        //>
        //    +-------------------------+
        //    | (clipped above SR)      |            ^
        //    |=========================| dst_top    |
        //    | dst (still in SR)       |            |
        //    +-------------------------+ src_top    |
        //    | src (moved up) and dst  |            |
        //    |-------------------------| dst_bot    |
        //    | src (invalid)           |            |
        //    +=========================+ src_bot
        //<
        //    If `rows` is less than zero, move a rectangle in the SR down, this can
        //    happen while scrolling up.
        //>
        //    +=========================+ src_top
        //    | src (invalid)           |            |
        //    |------------------------ | dst_top    |
        //    | src (moved down) and dst|            |
        //    +-------------------------+ src_bot    |
        //    | dst (still in SR)       |            |
        //    |=========================| dst_bot    |
        //    | (clipped below SR)      |            v
        //    +-------------------------+
        //<
        //    `cols` is always zero in this version of Nvim, and reserved for future
        //    use. 

        trace "scroll: %A %A %A %A %A %A" top bot left right rows cols

        let copy src dst =
            if src >= 0 && src < m_gridsize.rows && dst >= 0 && dst < m_gridsize.rows then
                Array.Copy(m_gridbuffer, src * m_gridsize.cols + left, m_gridbuffer, dst * m_gridsize.cols + left, right - left)
                markDirty {row = dst; height = 1; col = left; width = right - left }

        if rows > 0 then
            for i = top + rows to bot do
                copy i (i-rows)
        elif rows < 0 then
            for i = bot + rows - 1 downto top do
                copy i (i-rows)

        if top <= m_cursor_vm.row 
           && m_cursor_vm.row <= bot 
           && left <= m_cursor_vm.col 
           && m_cursor_vm.col <= right
        then
            this.cursorConfig()

    let setOption (opt: UiOption) = 
        trace "setOption: %A" opt

        let (|FN|_|) (x: string) =
            // try to parse with 'font\ name:hNN'
            match x.Split(':') with
            | [|name; size|] when size.Length > 0 && size.[0] = 'h' -> Some(name.Trim('\'', '"'), size.Substring(1).TrimEnd('\'','"') |> float)
            | _ -> None

        let mutable config_font = true

        match opt with
        | Guifont(FN(name, sz))             -> m_guifont     <- name; m_fontsize <- sz
        | GuifontWide(FN(name, sz))         -> m_guifontwide <- name; m_fontsize <- sz
        | Guifont("+") | GuifontWide("+")   -> m_fontsize    <- m_fontsize + 1.0
        | Guifont("-") | GuifontWide("-")   -> m_fontsize    <- m_fontsize - 1.0
        | Guifont(".+") | GuifontWide(".+") -> m_fontsize    <- m_fontsize + 0.1
        | Guifont(".-") | GuifontWide(".-") -> m_fontsize    <- m_fontsize - 0.1
        | _                                 -> config_font  <- false

        if config_font then fontConfig()

    let setMouse (en:bool) =
        m_mouse_en <- en

    let hiattrDefine (hls: HighlightAttr[]) =
        Array.iter setHighlight hls

    let setWinPos grid win startrow startcol w h =
        trace "setWinPos: grid = %A, win = %A, startrow = %A, startcol = %A, w = %A, h = %A" grid win startrow startcol w h
        let existing =  m_child_grids 
                     |> Seq.indexed
                     |> Seq.tryPick (function | (i, a) when (a :> IGridUI).Id = grid  -> Some(i, a)
                                              | _ -> None)
        let origin: Point = this.GetPoint startrow startcol
        let child_size    = this.GetPoint h w
        trace "setWinPos: child will be positioned at %A" origin
        match existing with
        | Some(i, child) -> 
            (* manually resize and position the child grid as per neovim docs *)
            trace "setWinPos: update parameters: h = %d w = %d X = %f Y = %f" h w origin.X origin.Y
            child.initBuffer h w
            child.X <- origin.X
            child.Y <- origin.Y
        | None -> 
            let child = new EditorViewModel(
                            grid, this, {rows=h; cols=w}, m_glyphsize, 
                            Size(child_size.X, child_size.Y), m_fontsize, m_gridscale, m_hi_defs, m_mode_defs, 
                            m_guifont, m_guifontwide, m_cursor_vm.modeidx, origin.X, origin.Y)
            m_child_grids.Add child
            //let wnd = Window()
            //wnd.Height  <- child_size.Y
            //wnd.Width   <- child_size.X
            //wnd.Content <- anchor.child
            //wnd.Show()

    let hidePopupMenu() =
        m_popupmenu_vm.Show <- false

    let selectPopupMenuPassive i =
        m_popupmenu_vm.Selection <- i

    let selectPopupMenuActive i =
        Model.SelectPopupMenuItem i true false

    let commitPopupMenu i =
        Model.SelectPopupMenuItem i true true

    let showPopupMenu grid (items: CompleteItem[]) selected row col =
        if grid <> GridId then
            hidePopupMenu()
        else
        let startPos  = this.GetPoint row col
        let cursorPos = this.GetPoint (m_cursor_vm.row + 1) m_cursor_vm.col

        trace "show popup menu at [%O, %O]" startPos cursorPos

        //  Decide the maximum size of the popup menu based on grid dimensions
        let menuLines = items.Length
        let menuCols = 
            items
            |> Array.map CompleteItem.GetLength
            |> Array.max

        let bounds = this.GetPoint menuLines menuCols
        let editorSize = this.GetPoint m_gridsize.rows m_gridsize.cols

        m_popupmenu_vm.Selection <- selected
        m_popupmenu_vm.SetItems(items, Rect(startPos, cursorPos), m_glyphsize.Height, bounds, editorSize)
        m_popupmenu_vm.Show <- true

    let redraw(cmd: RedrawCommand) =
        //trace "%A" cmd
        match cmd with
        | UnknownCommand x                                                   -> trace "unknown command %A" x
        | HighlightAttrDefine hls                                            -> hiattrDefine hls
        | SemanticHighlightGroupSet groups                                   -> setSemanticHighlightGroups groups
        | DefaultColorsSet(fg,bg,sp,_,_)                                     -> setDefaultColors fg bg sp
        | ModeInfoSet(cs_en, info)                                           -> setModeInfo cs_en info
        | ModeChange(name, index)                                            -> changeMode name index
        | GridResize(id, w, h)                                               -> if id = GridId then this.initBuffer h w
        | GridClear id                                                       -> if id = GridId then clearBuffer()
        | GridLine lines                                                     -> Array.iter (fun (line: GridLine) -> if line.grid = GridId then putBuffer line) lines
        | GridCursorGoto(id, row, col)                                       -> cursorGoto id row col
        | GridDestroy id                                                     -> ()
        | GridScroll(id, top,bot,left,right,rows,cols)                       -> if id = GridId then scrollBuffer top bot left right rows cols
        | Flush                                                              -> m_tick_ev.Trigger()
        | Bell                                                               -> bell false
        | VisualBell                                                         -> bell true
        | Busy is_busy                                                       -> setBusy is_busy
        | SetTitle title                                                     -> States.SetTitle title
        | SetIcon icon                                                       -> trace "icon: %s" icon // TODO
        | SetOption opts                                                     -> Array.iter setOption opts
        | Mouse en                                                           -> setMouse en
        | WinPos(grid, win, startrow, startcol, w, h)                        -> if GridId = 1 then setWinPos grid win startrow startcol w h
        | PopupMenuShow(items, selected, row, col, grid)                     -> showPopupMenu grid items selected row col
        | PopupMenuSelect(selected)                                          -> selectPopupMenuPassive selected
        | PopupMenuHide                                                      -> hidePopupMenu ()
        | x -> trace "unimplemented command: %A" x

    let raiseInputEvent e =
        m_input_ev.Trigger(GridId, e)

    do
        let fg,bg,sp,_ = this.GetDrawAttrs 0
        m_default_bg <- bg
        m_default_fg <- fg
        m_default_sp <- sp
        fontConfig()

        let tick_throttle =
            if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then id
            else Observable.throttle(TimeSpan.FromMilliseconds 10.0)

        this.Watch [
            Model.Redraw (Array.iter redraw)

            m_popupmenu_vm.ObservableForProperty(fun x -> x.Selection)
            |> Observable.subscribe (fun x -> selectPopupMenuActive <| x.GetValue())

            m_popupmenu_vm.Commit
            |> Observable.subscribe commitPopupMenu

            m_hlchange_ev.Publish 
            |> Observable.throttle(TimeSpan.FromMilliseconds 100.0) 
            |> Observable.subscribe markAllDirty

            m_tick_ev.Publish
            |> tick_throttle
            |> Observable.observeOn Avalonia.Threading.AvaloniaScheduler.Instance
            |> Observable.subscribe flush

            States.Register.Notify "ToggleFullScreen" (fun [| Integer32(gridid) |] -> toggleFullScreen gridid )
            States.Register.Watch "font" fontConfig

        ] 

    member __.Item with get(row, col) = m_gridbuffer.[row, col]

    member __.Cols with get() = m_gridsize.cols

    member __.Rows with get() = m_gridsize.rows

    member __.Dirty with get() = m_griddirty

    member __.MarkAllDirty() = markAllDirty()

    member __.GetFontAttrs() =
        m_guifont, m_guifontwide, m_fontsize

    member __.GetDrawAttrs hlid = 
        let attrs = m_hi_defs.[hlid].rgb_attr

        let mutable fg = Option.defaultValue m_default_fg attrs.foreground
        let mutable bg = Option.defaultValue m_default_bg attrs.background
        let mutable sp = Option.defaultValue m_default_sp attrs.special

        if attrs.reverse then
            fg <- GetReverseColor fg
            bg <- GetReverseColor bg
            sp <- GetReverseColor sp

        fg, bg, sp, attrs


    member private __.initBuffer nrow ncol =
        m_gridsize <- { rows = nrow; cols = ncol }
        trace "buffer resize = %A" m_gridsize
        clearBuffer()

    interface IGridUI with
        member __.Id = GridId
        member __.GridHeight = int( this.Height / m_glyphsize.Height )
        member __.GridWidth  = int( this.Width  / m_glyphsize.Width  )
        member __.Resized = m_resize_ev.Publish
        member __.Input = m_input_ev.Publish
        member __.HasChildren = m_child_grids.Count <> 0

    member __.markClean = m_griddirty.Clear

    //  converts grid position to UI Point
    member __.GetPoint row col =
        Point(double(col) * m_glyphsize.Width, double(row) * m_glyphsize.Height)

    member __.cursorConfig() =
        if m_mode_defs.Length = 0 || m_cursor_vm.modeidx < 0 then ()
        elif m_gridbuffer.GetLength(0) <= m_cursor_vm.row || m_gridbuffer.GetLength(1) <= m_cursor_vm.col then ()
        else
        let mode              = m_mode_defs.[m_cursor_vm.modeidx]
        let hlid              = m_gridbuffer.[m_cursor_vm.row, m_cursor_vm.col].hlid
        let hlid              = Option.defaultValue hlid mode.attr_id
        let fg, bg, sp, attrs = this.GetDrawAttrs hlid
        let origin            = this.GetPoint m_cursor_vm.row m_cursor_vm.col
        let text              = m_gridbuffer.[m_cursor_vm.row, m_cursor_vm.col].text
        let text_type         = wswidth text
        let width             = float(max <| 1 <| CharTypeWidth text_type) * m_glyphsize.Width

        let on, off, wait =
            match mode with
            | { blinkon = Some on; blinkoff = Some off; blinkwait = Some wait  }
                when on > 0 && off > 0 && wait > 0 -> on, off, wait
            | _ -> 0,0,0

        // do not use the default colors for cursor
        let colorf = if hlid = 0 then GetReverseColor else id
        let fg, bg, sp = colorf fg, colorf bg, colorf sp
        m_cursor_vm.typeface       <- m_guifont
        m_cursor_vm.wtypeface      <- m_guifontwide
        m_cursor_vm.fontSize       <- m_fontsize
        m_cursor_vm.text           <- text
        m_cursor_vm.fg             <- fg
        m_cursor_vm.bg             <- bg
        m_cursor_vm.sp             <- sp
        m_cursor_vm.underline      <- attrs.underline
        m_cursor_vm.undercurl      <- attrs.undercurl
        m_cursor_vm.bold           <- attrs.bold
        m_cursor_vm.italic         <- attrs.italic
        m_cursor_vm.cellPercentage <- Option.defaultValue 100 mode.cell_percentage
        m_cursor_vm.blinkon        <- on
        m_cursor_vm.blinkoff       <- off
        m_cursor_vm.blinkwait      <- wait
        m_cursor_vm.shape          <- Option.defaultValue CursorShape.Block mode.cursor_shape
        m_cursor_vm.X              <- origin.X
        m_cursor_vm.Y              <- origin.Y
        m_cursor_vm.Width          <- width
        m_cursor_vm.Height         <- m_glyphsize.Height
        m_cursor_vm.RenderTick     <- m_cursor_vm.RenderTick + 1
        trace "set cursor info, color = %A %A %A" fg bg sp

    member this.setCursorEnabled v =
        m_cursor_vm.enabled <- v
        m_cursor_vm.RenderTick <- m_cursor_vm.RenderTick + 1

    (*******************   Exposed properties   ***********************)

    member this.Fullscreen
        with get() : bool = m_gridfullscreen
        and set(v) =
            ignore <| this.RaiseAndSetIfChanged(&m_gridfullscreen, v)

    member this.CursorInfo
        with get() : CursorViewModel = m_cursor_vm

    member this.PopupMenu
        with get(): PopupMenuViewModel = m_popupmenu_vm

    member __.RenderScale
        with get() : float = m_gridscale
        and set(v) =
            m_gridscale <- v

    member __.BackgroundBrush
        with get(): SolidColorBrush = SolidColorBrush(m_default_bg)

    member __.BufferHeight with get(): float = m_fb_h
    member __.BufferWidth  with get(): float = m_fb_w
    member __.GlyphHeight with get(): float = m_glyphsize.Height
    member __.GlyphWidth with get(): float = m_glyphsize.Width
    member __.TopLevel with get(): bool  = parent.IsNone

    member __.GridId
        with get() = GridId

    member __.ChildGrids = m_child_grids

    member this.SetMeasuredSize (v: Size) =
        trace "set measured size: %A" v
        let gridui = this :> IGridUI
        let gw, gh = gridui.GridWidth, gridui.GridHeight
        this.Width <- v.Width
        this.Height <- v.Height
        let gw', gh' = gridui.GridWidth, gridui.GridHeight
        if gw <> gw' || gh <> gh' then 
            if this.TopLevel then
                m_resize_ev.Trigger(this)

    (*******************   Events   ***********************)

    member __.OnKey (e: KeyEventArgs) = 
        raiseInputEvent <| InputEvent.Key(e.Modifiers, e.Key)

    member __.OnMouseDown (e: PointerPressedEventArgs) (root: Avalonia.VisualTree.IVisual) = 
        if m_mouse_en then
            let x, y = e.GetPosition root |> getPos
            m_mouse_pressed <- e.MouseButton
            raiseInputEvent <| InputEvent.MousePress(e.InputModifiers, y, x, e.MouseButton, e.ClickCount)

    member __.OnMouseUp (e: PointerReleasedEventArgs) (root: Avalonia.VisualTree.IVisual) = 
        if m_mouse_en then
            let x, y = e.GetPosition root |> getPos
            m_mouse_pressed <- MouseButton.None
            raiseInputEvent <| InputEvent.MouseRelease(e.InputModifiers, y, x, e.MouseButton)

    member __.OnMouseMove (e: PointerEventArgs) (root: Avalonia.VisualTree.IVisual) = 
        if m_mouse_en && m_mouse_pressed <> MouseButton.None then
            let x, y = e.GetPosition root |> getPos
            if (x,y) <> m_mouse_pos then
                m_mouse_pos <- x,y
                raiseInputEvent <| InputEvent.MouseDrag(e.InputModifiers, y, x, m_mouse_pressed)

    member __.OnMouseWheel (e: PointerWheelEventArgs) (root: Avalonia.VisualTree.IVisual) = 
        if m_mouse_en then
            let x, y = e.GetPosition root |> getPos
            let dx, dy = e.Delta.X, e.Delta.Y
            raiseInputEvent <| InputEvent.MouseWheel(e.InputModifiers, y, x, dx, dy)

    member __.OnTextInput (e: TextInputEventArgs) = 
        raiseInputEvent <| InputEvent.TextInput(e.Text)

