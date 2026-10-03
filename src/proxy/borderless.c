// Generals Borderless - dinput8.dll proxy for C&C Generals / Zero Hour and every mod that runs
// their game.dat. The game loads dinput8.dll from its own folder, so this code runs inside the
// game no matter which launcher starts it. It forwards DirectInput to the real dinput8.dll and
// makes the game borderless fullscreen:
//   - adds -win to the command line: exclusive fullscreen hitches on current Windows 11, windowed doesn't;
//   - creates the game window without a frame and keeps it centred on its monitor, which at the
//     monitor's resolution means covering it exactly;
//   - sets the resolution in Options.ini to the monitor's before the game reads it;
//   - makes the game DPI aware, so Windows display scaling doesn't blow the window up;
//   - keeps the mouse inside the game while it has focus (edge scrolling, second monitors).
// Settings: GeneralsBorderless.ini next to this DLL. Log: %LOCALAPPDATA%\GeneralsBorderless\game.log
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>

// The patcher recognises its own DLL by this text, so keep it in the binary.
static const char Marker[] = "GeneralsBorderless dinput8 proxy 1.4.1";
static const DWORD FrameStyles = WS_CAPTION | WS_THICKFRAME;
static const DWORD FrameExStyles = WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE;

static HMODULE self, realDInput;
static HWND gameWnd, backdrop;
static BOOL managed, forceNativeRes = TRUE, lockCursor = TRUE, scaleToScreen = TRUE;
// The size the game gave its window, which is what it renders at; with ScaleToScreen the real window
// is that size scaled up to fill the monitor, and the game is shown its own size and mouse positions.
static int gameW, gameH;
static BOOL gameSized; // set once the game sized its window for its resolution (after the splash screen)
static WNDPROC gameProc;
static char logPath[MAX_PATH];

static int (__cdecl *realGetMainArgs)(int *, char ***, char ***, int, void *);
static LPSTR (WINAPI *realGetCommandLineA)(void);
static HWND (WINAPI *realCreateWindowExA)(DWORD, LPCSTR, LPCSTR, DWORD, int, int, int, int, HWND, HMENU, HINSTANCE, LPVOID);
static BOOL (WINAPI *realSetWindowPos)(HWND, HWND, int, int, int, int, UINT);
static BOOL (WINAPI *realClipCursor)(const RECT *);
static BOOL (WINAPI *realGetClientRect)(HWND, RECT *);
static BOOL (WINAPI *realGetWindowRect)(HWND, RECT *);
static BOOL (WINAPI *realGetCursorPos)(POINT *);

static void Log(const char *fmt, ...)
{
    char line[1024];
    SYSTEMTIME t;
    GetLocalTime(&t);
    int n = wsprintfA(line, "%04d-%02d-%02d %02d:%02d:%02d.%03d [%lu] ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId());
    va_list args;
    va_start(args, fmt);
    n += wvsprintfA(line + n, fmt, args);
    va_end(args);
    lstrcpyA(line + n, "\r\n");
    HANDLE f = CreateFileA(logPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    if (GetFileSize(f, NULL) > 512 * 1024) SetEndOfFile(f); // start over rather than grow forever
    else SetFilePointer(f, 0, NULL, FILE_END);
    DWORD written;
    WriteFile(f, line, n + 2, &written, NULL);
    CloseHandle(f);
}

// A file next to this DLL.
static void SiblingPath(char *path, const char *name)
{
    GetModuleFileNameA(self, path, MAX_PATH - lstrlenA(name));
    char *slash = path;
    for (char *p = path; *p; p++)
        if (*p == '\\') slash = p + 1;
    lstrcpyA(slash, name);
}

// Points the main exe's import of dll!name at hook and returns the previous target, or NULL when
// the exe doesn't import it.
static void *HookImport(HMODULE exe, const char *dll, const char *name, void *hook)
{
    BYTE *base = (BYTE *)exe;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return NULL;
    HMODULE target = GetModuleHandleA(dll);
    void *resolved = target ? (void *)GetProcAddress(target, name) : NULL;
    for (IMAGE_IMPORT_DESCRIPTOR *d = (IMAGE_IMPORT_DESCRIPTOR *)(base + dir.VirtualAddress); d->Name; d++)
    {
        if (lstrcmpiA((char *)(base + d->Name), dll)) continue;
        IMAGE_THUNK_DATA *names = (IMAGE_THUNK_DATA *)(base + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        IMAGE_THUNK_DATA *slots = (IMAGE_THUNK_DATA *)(base + d->FirstThunk);
        for (; names->u1.AddressOfData; names++, slots++)
        {
            // Match by name when the exe keeps its name table (compatibility shims may have rerouted
            // the slot itself), otherwise by the address the slot was bound to.
            BOOL match = d->OriginalFirstThunk
                ? !IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal) && !lstrcmpA((char *)((IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData))->Name, name)
                : resolved && (void *)slots->u1.Function == resolved;
            if (!match) continue;
            void *previous = (void *)slots->u1.Function;
            DWORD protect;
            VirtualProtect(&slots->u1.Function, sizeof(void *), PAGE_READWRITE, &protect);
            slots->u1.Function = (ULONG_PTR)hook;
            VirtualProtect(&slots->u1.Function, sizeof(void *), protect, &protect);
            return previous;
        }
    }
    return NULL;
}

static BOOL ImageContains(HMODULE module, const char *text)
{
    BYTE *base = (BYTE *)module;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    IMAGE_SECTION_HEADER *sec = IMAGE_FIRST_SECTION(nt);
    int len = lstrlenA(text) + 1; // include the terminator: whole strings only
    for (int i = 0; i < nt->FileHeader.NumberOfSections; i++)
    {
        BYTE *p = base + sec[i].VirtualAddress;
        DWORD size = sec[i].Misc.VirtualSize;
        for (DWORD j = 0; j + len <= size; j++)
        {
            int k = 0;
            while (k < len && p[j + k] == (BYTE)text[k]) k++;
            if (k == len) return TRUE;
        }
    }
    return FALSE;
}

static void CenterOnMonitor(HMONITOR monitor, int w, int h, int *x, int *y)
{
    MONITORINFO mi = { sizeof mi };
    GetMonitorInfoA(monitor, &mi);
    int mw = mi.rcMonitor.right - mi.rcMonitor.left, mh = mi.rcMonitor.bottom - mi.rcMonitor.top;
    *x = mi.rcMonitor.left + (w < mw ? (mw - w) / 2 : 0);
    *y = mi.rcMonitor.top + (h < mh ? (mh - h) / 2 : 0);
}

static BOOL GameActive(void)
{
    return gameWnd && GetForegroundWindow() == gameWnd && !IsIconic(gameWnd);
}

// Where the real window goes for a game size of w x h: scaled up (or down) to fit the monitor while
// keeping its shape, or at its own size (box mode, and the splash screen) - centred either way.
static void TargetRect(HMONITOR monitor, int w, int h, RECT *r, RECT *mon)
{
    MONITORINFO mi = { sizeof mi };
    GetMonitorInfoA(monitor, &mi);
    *mon = mi.rcMonitor;
    int mw = mi.rcMonitor.right - mi.rcMonitor.left, mh = mi.rcMonitor.bottom - mi.rcMonitor.top;
    if (scaleToScreen && gameSized && w > 0 && h > 0 && (w != mw || h != mh))
    {
        if ((long long)mw * h <= (long long)mh * w) { h = (int)((long long)h * mw / w); w = mw; } // fits the width
        else { w = (int)((long long)w * mh / h); h = mh; }                                       // fits the height
    }
    int x, y;
    CenterOnMonitor(monitor, w, h, &x, &y);
    SetRect(r, x, y, x + w, y + h);
}

// When the real window is bigger or smaller than the size the game thinks it has: the real window rect
// and the scale per axis. The game's coordinates use the same top left corner, only scaled.
static BOOL Scaled(RECT *real, double *sx, double *sy)
{
    if (!managed || !gameW || !gameH || !GetWindowRect(gameWnd, real)) return FALSE;
    int rw = real->right - real->left, rh = real->bottom - real->top;
    if (rw == gameW && rh == gameH) return FALSE;
    *sx = (double)rw / gameW;
    *sy = (double)rh / gameH;
    return TRUE;
}

static void ToGame(POINT *p, const RECT *real, double sx, double sy)
{
    p->x = real->left + (int)((p->x - real->left) / sx);
    p->y = real->top + (int)((p->y - real->top) / sy);
}

static void ToReal(POINT *p, const RECT *real, double sx, double sy)
{
    p->x = real->left + (int)((p->x - real->left) * sx);
    p->y = real->top + (int)((p->y - real->top) * sy);
}

// Documents\<user data folder>\Options.ini, the folder name as the game itself picks it.
static BOOL OptionsPath(char *path)
{
    char leaf[MAX_PATH] = "Command and Conquer Generals Data";
    if (ImageContains(GetModuleHandleA(NULL), "Command and Conquer Generals Zero Hour Data"))
    {
        lstrcpyA(leaf, "Command and Conquer Generals Zero Hour Data");
        HKEY key;
        if (!RegOpenKeyExA(HKEY_LOCAL_MACHINE, "SOFTWARE\\Electronic Arts\\EA Games\\Command and Conquer Generals Zero Hour", 0, KEY_READ, &key))
        {
            char value[MAX_PATH];
            DWORD type, size = sizeof value - 1;
            if (!RegQueryValueExA(key, "UserDataLeafName", NULL, &type, (BYTE *)value, &size) && type == REG_SZ && size > 1)
            {
                value[size] = 0;
                lstrcpyA(leaf, value);
            }
            RegCloseKey(key);
        }
    }
    if (FAILED(SHGetFolderPathA(NULL, CSIDL_PERSONAL, NULL, 0, path))) return FALSE;
    lstrcatA(path, "\\");
    lstrcatA(path, leaf);
    CreateDirectoryA(path, NULL);
    lstrcatA(path, "\\Options.ini");
    return TRUE;
}

static BOOL IsResolutionLine(const char *p)
{
    while (*p == ' ' || *p == '\t') p++;
    if (CompareStringA(LOCALE_INVARIANT, NORM_IGNORECASE, p, 10, "Resolution", 10) != CSTR_EQUAL) return FALSE;
    p += 10;
    while (*p == ' ' || *p == '\t') p++;
    return *p == '=';
}

// The window can only cover the monitor exactly when the game renders at the monitor's resolution.
static void SetNativeResolution(void)
{
    DEVMODEA mode = { .dmSize = sizeof mode };
    char path[MAX_PATH], want[64];
    if (!EnumDisplaySettingsA(NULL, ENUM_CURRENT_SETTINGS, &mode) || !OptionsPath(path)) return;
    int wantLen = wsprintfA(want, "Resolution = %lu %lu", mode.dmPelsWidth, mode.dmPelsHeight);

    enum { Max = 256 * 1024 };
    char *text = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, Max + 1), *out = HeapAlloc(GetProcessHeap(), 0, Max + 128);
    DWORD len = 0;
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f != INVALID_HANDLE_VALUE)
    {
        ReadFile(f, text, Max, &len, NULL);
        CloseHandle(f);
    }
    char *line = NULL;
    for (char *p = text; p < text + len; p++)
        if ((p == text || p[-1] == '\n') && IsResolutionLine(p)) { line = p; break; }
    char *end = line;
    while (end && end < text + len && *end != '\r' && *end != '\n') end++;

    if (line && end - line == wantLen && CompareStringA(LOCALE_INVARIANT, 0, line, wantLen, want, wantLen) == CSTR_EQUAL)
        goto done;
    DWORD n = 0;
    if (line)
    {
        CopyMemory(out, text, line - text); n = line - text;
        CopyMemory(out + n, want, wantLen); n += wantLen;
        CopyMemory(out + n, end, text + len - end); n += text + len - end;
    }
    else
    {
        CopyMemory(out, text, len); n = len;
        if (n && out[n - 1] != '\n') { out[n++] = '\r'; out[n++] = '\n'; }
        CopyMemory(out + n, want, wantLen); n += wantLen;
        out[n++] = '\r'; out[n++] = '\n';
    }
    f = CreateFileA(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f != INVALID_HANDLE_VALUE)
    {
        DWORD written;
        WriteFile(f, out, n, &written, NULL);
        CloseHandle(f);
        Log("Options.ini: %s (%s)", want, path);
    }
    else Log("Options.ini: cannot write %s (error %lu)", path, GetLastError());
done:
    HeapFree(GetProcessHeap(), 0, text);
    HeapFree(GetProcessHeap(), 0, out);
}

static void MakeThreadDpiAware(void)
{
    HANDLE (WINAPI *setThread)(HANDLE) = (void *)GetProcAddress(GetModuleHandleA("user32.dll"), "SetThreadDpiAwarenessContext");
    if (setThread) setThread((HANDLE)-4); // per-monitor v2
}

static int ThreadDpiAwareness(void)
{
    HMODULE user32 = GetModuleHandleA("user32.dll");
    HANDLE (WINAPI *getThread)(void) = (void *)GetProcAddress(user32, "GetThreadDpiAwarenessContext");
    int (WINAPI *awarenessOf)(HANDLE) = (void *)GetProcAddress(user32, "GetAwarenessFromDpiAwarenessContext");
    return getThread && awarenessOf ? awarenessOf(getThread()) : -1;
}

// Runs once, before the game creates its window or reads Options.ini.
static void Prepare(void)
{
    static BOOL done;
    if (done) return;
    done = TRUE;
    HMODULE user32 = GetModuleHandleA("user32.dll");
    BOOL (WINAPI *setContext)(HANDLE) = (void *)GetProcAddress(user32, "SetProcessDpiAwarenessContext");
    BOOL (WINAPI *setAware)(void) = (void *)GetProcAddress(user32, "SetProcessDPIAware");
    BOOL processSet = setContext && setContext((HANDLE)-4); // per-monitor v2
    if (!setContext && setAware) setAware();                // older Windows: system aware
    // The process setting can already be locked (Steam's build ends up DPI unaware), but the game
    // thread's own setting still decides how its window is scaled, so set that too.
    MakeThreadDpiAware();
    Log("DPI aware: process %s, game thread %d (2 = per monitor)", processSet ? "set" : "already fixed", ThreadDpiAwareness());
    if (forceNativeRes) SetNativeResolution();
}

static BOOL HasWinArg(int argc, char **argv)
{
    for (int i = 1; i < argc; i++)
        if (!lstrcmpiA(argv[i], "-win")) return TRUE;
    return FALSE;
}

static char *WithWin(const char *cmdline)
{
    char *s = HeapAlloc(GetProcessHeap(), 0, lstrlenA(cmdline) + 6);
    lstrcpyA(s, cmdline);
    lstrcatA(s, " -win");
    return s;
}

// The game's CRT startup gets argv here and WinMain's command line from _acmdln just after.
static int __cdecl HookGetMainArgs(int *argc, char ***argv, char ***envp, int wildcard, void *startInfo)
{
    int result = realGetMainArgs(argc, argv, envp, wildcard, startInfo);
    Prepare();
    if (result >= 0 && !HasWinArg(*argc, *argv))
    {
        char **args = HeapAlloc(GetProcessHeap(), 0, (*argc + 2) * sizeof(char *));
        CopyMemory(args, *argv, *argc * sizeof(char *));
        args[*argc] = "-win";
        args[*argc + 1] = NULL;
        *argv = args;
        *argc += 1;
        HMODULE crt = GetModuleHandleA("msvcrt.dll");
        int *crtArgc = (int *)GetProcAddress(crt, "__argc");
        char ***crtArgv = (char ***)GetProcAddress(crt, "__argv");
        char **cmdline = (char **)GetProcAddress(crt, "_acmdln");
        if (crtArgc) *crtArgc = *argc;
        if (crtArgv) *crtArgv = args;
        if (cmdline && *cmdline) *cmdline = WithWin(*cmdline);
        Log("added -win to the command line");
    }
    return result;
}

// For builds that read the command line straight from kernel32 instead of msvcrt.
static LPSTR WINAPI HookGetCommandLineA(void)
{
    static char *cmdline;
    Prepare();
    if (!cmdline)
    {
        char *orig = realGetCommandLineA();
        int argc = 0;
        LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        BOOL has = FALSE;
        for (int i = 1; argv && i < argc; i++)
            if (!lstrcmpiW(argv[i], L"-win")) has = TRUE;
        if (argv) LocalFree(argv);
        cmdline = has ? orig : WithWin(orig);
        if (!has) Log("added -win to the command line");
    }
    return cmdline;
}

// Keeps the window frameless and centred (in case something we don't hook moves it) and the
// cursor inside it while it has focus.
static DWORD WINAPI Worker(LPVOID unused)
{
    BOOL clipped = FALSE;
    MakeThreadDpiAware(); // same pixel coordinates as the game thread
    while (IsWindow(gameWnd))
    {
        if (!IsIconic(gameWnd))
        {
            LONG style = GetWindowLongA(gameWnd, GWL_STYLE), exStyle = GetWindowLongA(gameWnd, GWL_EXSTYLE);
            BOOL framed = (style & FrameStyles) || (exStyle & FrameExStyles);
            if (framed)
            {
                SetWindowLongA(gameWnd, GWL_STYLE, (style & ~FrameStyles) | WS_POPUP);
                SetWindowLongA(gameWnd, GWL_EXSTYLE, exStyle & ~FrameExStyles);
            }
            RECT now, target, mon;
            GetWindowRect(gameWnd, &now);
            TargetRect(MonitorFromWindow(gameWnd, MONITOR_DEFAULTTONEAREST), gameW, gameH, &target, &mon);
            int x = target.left, y = target.top, w = target.right - target.left, h = target.bottom - target.top;
            if (backdrop)
            {
                // black behind the game where scaling leaves bars (a 4:3 game on a wide screen);
                // not behind the splash screen, and not while the game is hidden
                BOOL bars = gameSized && IsWindowVisible(gameWnd) && !EqualRect(&target, &mon);
                if (bars != (IsWindowVisible(backdrop) != 0))
                    realSetWindowPos(backdrop, NULL, mon.left, mon.top, mon.right - mon.left, mon.bottom - mon.top,
                                     SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | (bars ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
                if (bars && GetWindow(gameWnd, GW_HWNDNEXT) != backdrop)
                    realSetWindowPos(backdrop, gameWnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            }
            if (framed || !EqualRect(&now, &target))
            {
                realSetWindowPos(gameWnd, NULL, x, y, w, h, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | (framed ? SWP_FRAMECHANGED : 0));
                Log("window %s to %dx%d at %d,%d", framed ? "made frameless" : "re-centred", w, h, x, y);
                // GenTool moves the window (to the top left, below native resolution) and locks the mouse
                // to it there; after the move that lock is in the wrong place. LockCursor re-locks it below.
                RECT clip;
                if (!lockCursor && GetClipCursor(&clip) && EqualRect(&clip, &now))
                {
                    realClipCursor(NULL);
                    Log("released a mouse lock left at the old window position");
                }
            }
        }
        else if (backdrop && IsWindowVisible(backdrop))
            ShowWindowAsync(backdrop, SW_HIDE); // minimized (Win+D, taskbar): no black screen over the desktop
        if (lockCursor)
        {
            if (GameActive())
            {
                RECT w, current;
                GetWindowRect(gameWnd, &w);
                GetClipCursor(&current);
                if (!EqualRect(&w, &current)) realClipCursor(&w);
                clipped = TRUE;
            }
            else if (clipped)
            {
                realClipCursor(NULL);
                clipped = FALSE;
            }
        }
        Sleep(100);
    }
    if (clipped) realClipCursor(NULL);
    if (backdrop) ShowWindowAsync(backdrop, SW_HIDE);
    return 0;
}

// The game reads mouse positions from its window messages; when the window is scaled, hand it
// positions in its own (unscaled) size.
static LRESULT CALLBACK MouseScaleProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    RECT real;
    double sx, sy;
    if (msg >= WM_MOUSEFIRST && msg <= 0x020E && Scaled(&real, &sx, &sy))
    {
        POINT p = { (short)LOWORD(lParam), (short)HIWORD(lParam) };
        if (msg == WM_MOUSEWHEEL || msg == 0x020E) ToGame(&p, &real, sx, sy); // screen coordinates (0x020E: horizontal wheel)
        else { p.x = (int)(p.x / sx); p.y = (int)(p.y / sy); }               // window coordinates
        lParam = MAKELPARAM((short)p.x, (short)p.y);
    }
    return CallWindowProcA(gameProc, hwnd, msg, wParam, lParam);
}

static HWND CreateBackdrop(void)
{
    WNDCLASSA wc = { 0 };
    wc.lpfnWndProc = DefWindowProcA;
    wc.hInstance = self;
    wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
    wc.hCursor = LoadCursorA(NULL, (LPCSTR)IDC_ARROW);
    wc.lpszClassName = "GeneralsBorderlessBackdrop";
    RegisterClassA(&wc);
    return CreateWindowExA(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, "", WS_POPUP, 0, 0, 1, 1, NULL, NULL, self, NULL);
}

static HWND WINAPI HookCreateWindowExA(DWORD exStyle, LPCSTR cls, LPCSTR title, DWORD style, int x, int y, int w, int h, HWND parent, HMENU menu, HINSTANCE inst, LPVOID param)
{
    BOOL game = !gameWnd && !parent && HIWORD((ULONG_PTR)cls) && !lstrcmpiA(cls, "Game Window");
    BOOL windowed = game && (style & WS_CAPTION) == WS_CAPTION;
    if (game) Prepare();
    if (windowed)
    {
        // The game made the size room for a frame (AdjustWindowRect around 800x600); without the frame
        // that room would show as a black edge under the splash screen, so take it off again.
        RECT frame = { 0, 0, 0, 0 };
        if (AdjustWindowRectEx(&frame, style, FALSE, exStyle) && w > frame.right - frame.left && h > frame.bottom - frame.top)
        {
            w -= frame.right - frame.left;
            h -= frame.bottom - frame.top;
        }
        style = (style & ~FrameStyles) | WS_POPUP;
        exStyle &= ~FrameExStyles;
        POINT centre = { x + w / 2, y + h / 2 };
        CenterOnMonitor(MonitorFromPoint(centre, MONITOR_DEFAULTTOPRIMARY), w, h, &x, &y);
        gameW = w;
        gameH = h;
        if (scaleToScreen) backdrop = CreateBackdrop(); // on the game's thread, so its message loop paints it
    }
    HWND hwnd = realCreateWindowExA(exStyle, cls, title, style, x, y, w, h, parent, menu, inst, param);
    if (game && hwnd)
    {
        gameWnd = hwnd;
        managed = windowed;
        Log("game window created %s (style %08lX)", windowed ? "frameless" : "fullscreen, left alone", style);
        if (managed)
        {
            gameProc = (WNDPROC)SetWindowLongA(hwnd, GWL_WNDPROC, (LONG)MouseScaleProc);
            CloseHandle(CreateThread(NULL, 0, Worker, NULL, 0, NULL));
        }
    }
    return hwnd;
}

// The game centres its window in the work area (above the taskbar) or resizes it in place; keep it
// centred on the monitor instead.
static BOOL WINAPI HookSetWindowPos(HWND hwnd, HWND after, int x, int y, int cx, int cy, UINT flags)
{
    if (managed && hwnd == gameWnd && (flags & (SWP_NOMOVE | SWP_NOSIZE)) != (SWP_NOMOVE | SWP_NOSIZE))
    {
        if (!(flags & SWP_NOSIZE))
        {
            gameW = cx; // the game's size for its resolution: frameless, so window = client = resolution
            gameH = cy;
            gameSized = TRUE;
        }
        RECT target, mon;
        TargetRect(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), gameW, gameH, &target, &mon);
        x = target.left; y = target.top;
        cx = target.right - target.left; cy = target.bottom - target.top;
        flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
    }
    return realSetWindowPos(hwnd, after, x, y, cx, cy, flags);
}

// When the game lets go of the cursor while it still has focus, keep it inside the window; a lock the
// game sets itself is in its own (unscaled) coordinates.
static BOOL WINAPI HookClipCursor(const RECT *rect)
{
    if (managed && lockCursor && !rect && GameActive())
    {
        RECT w;
        GetWindowRect(gameWnd, &w);
        return realClipCursor(&w);
    }
    RECT real;
    double sx, sy;
    if (rect && gameWnd && Scaled(&real, &sx, &sy))
    {
        POINT a = { rect->left, rect->top }, b = { rect->right, rect->bottom };
        ToReal(&a, &real, sx, sy);
        ToReal(&b, &real, sx, sy);
        RECT r = { a.x, a.y, b.x, b.y };
        return realClipCursor(&r);
    }
    return realClipCursor(rect);
}

// The game is shown its own size and mouse position, not the scaled real ones.
static BOOL WINAPI HookGetClientRect(HWND hwnd, RECT *r)
{
    RECT real;
    double sx, sy;
    if (hwnd == gameWnd && Scaled(&real, &sx, &sy))
    {
        SetRect(r, 0, 0, gameW, gameH);
        return TRUE;
    }
    // The game first asks for its window's size when the splash screen is done, to fit the window to its
    // resolution. At 800x600, the splash screen's own size, nothing needs resizing (no SetWindowPos follows),
    // so this is where the game is known to have its final size.
    if (managed && hwnd == gameWnd) gameSized = TRUE;
    return realGetClientRect(hwnd, r);
}

static BOOL WINAPI HookGetWindowRect(HWND hwnd, RECT *r)
{
    RECT real;
    double sx, sy;
    if (hwnd == gameWnd && Scaled(&real, &sx, &sy))
    {
        SetRect(r, real.left, real.top, real.left + gameW, real.top + gameH);
        return TRUE;
    }
    return realGetWindowRect(hwnd, r);
}

static BOOL WINAPI HookGetCursorPos(POINT *p)
{
    BOOL ok = realGetCursorPos(p);
    RECT real;
    double sx, sy;
    if (ok && Scaled(&real, &sx, &sy)) ToGame(p, &real, sx, sy);
    return ok;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    self = inst;
    DisableThreadLibraryCalls(inst);

    char ini[MAX_PATH], exe[MAX_PATH];
    SiblingPath(ini, "GeneralsBorderless.ini");
    GetModuleFileNameA(NULL, exe, MAX_PATH);
    if (GetEnvironmentVariableA("LOCALAPPDATA", logPath, MAX_PATH - 40))
    {
        lstrcatA(logPath, "\\GeneralsBorderless");
        CreateDirectoryA(logPath, NULL);
        lstrcatA(logPath, "\\game.log");
    }

    HMODULE main = GetModuleHandleA(NULL);
    if (!ImageContains(main, "Game Window")) return TRUE; // not the game (proxy only)
    if (!GetPrivateProfileIntA("Borderless", "Enabled", 1, ini))
    {
        Log("%s: %s, disabled in GeneralsBorderless.ini", Marker, exe);
        return TRUE;
    }
    forceNativeRes = GetPrivateProfileIntA("Borderless", "ForceNativeResolution", 1, ini);
    lockCursor = GetPrivateProfileIntA("Borderless", "LockCursor", 1, ini);
    scaleToScreen = GetPrivateProfileIntA("Borderless", "ScaleToScreen", 1, ini);

    realGetMainArgs = HookImport(main, "msvcrt.dll", "__getmainargs", HookGetMainArgs);
    if (!realGetMainArgs) realGetCommandLineA = HookImport(main, "kernel32.dll", "GetCommandLineA", HookGetCommandLineA);
    realCreateWindowExA = HookImport(main, "user32.dll", "CreateWindowExA", HookCreateWindowExA);
    realSetWindowPos = HookImport(main, "user32.dll", "SetWindowPos", HookSetWindowPos);
    realClipCursor = HookImport(main, "user32.dll", "ClipCursor", HookClipCursor);
    realGetClientRect = HookImport(main, "user32.dll", "GetClientRect", HookGetClientRect);
    realGetWindowRect = HookImport(main, "user32.dll", "GetWindowRect", HookGetWindowRect);
    realGetCursorPos = HookImport(main, "user32.dll", "GetCursorPos", HookGetCursorPos);
    Log("%s: %s, hooks: args=%d window=%d move=%d cursor=%d scale=%d%d%d, ScaleToScreen=%d", Marker, exe,
        realGetMainArgs || realGetCommandLineA, realCreateWindowExA != NULL, realSetWindowPos != NULL, realClipCursor != NULL,
        realGetClientRect != NULL, realGetWindowRect != NULL, realGetCursorPos != NULL, scaleToScreen);
    if (!realSetWindowPos) realSetWindowPos = SetWindowPos;
    if (!realClipCursor) realClipCursor = ClipCursor;
    if (!realGetClientRect) realGetClientRect = GetClientRect;
    if (!realGetWindowRect) realGetWindowRect = GetWindowRect;
    if (!realGetCursorPos) realGetCursorPos = GetCursorPos;
    return TRUE;
}

// ---- DirectInput, forwarded to the real dinput8.dll (or to a dinput8_original.dll that was here before)

static FARPROC Real(const char *name)
{
    if (!realDInput)
    {
        char path[MAX_PATH];
        SiblingPath(path, "dinput8_original.dll");
        if (GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES) realDInput = LoadLibraryA(path);
        if (realDInput == self) realDInput = NULL; // this file itself, renamed by another tool: never call ourselves
        if (!realDInput && GetSystemDirectoryA(path, MAX_PATH - 20))
        {
            lstrcatA(path, "\\dinput8.dll");
            realDInput = LoadLibraryA(path);
        }
    }
    return realDInput ? GetProcAddress(realDInput, name) : NULL;
}

HRESULT WINAPI DirectInput8Create(HINSTANCE inst, DWORD version, const GUID *iid, void **out, void *outer)
{
    HRESULT (WINAPI *f)(HINSTANCE, DWORD, const GUID *, void **, void *) = (void *)Real("DirectInput8Create");
    return f ? f(inst, version, iid, out, outer) : E_FAIL;
}

HRESULT WINAPI DllCanUnloadNow(void)
{
    HRESULT (WINAPI *f)(void) = (void *)Real("DllCanUnloadNow");
    return f ? f() : S_FALSE;
}

HRESULT WINAPI DllGetClassObject(const GUID *clsid, const GUID *iid, void **out)
{
    HRESULT (WINAPI *f)(const GUID *, const GUID *, void **) = (void *)Real("DllGetClassObject");
    return f ? f(clsid, iid, out) : CLASS_E_CLASSNOTAVAILABLE;
}

HRESULT WINAPI DllRegisterServer(void)
{
    HRESULT (WINAPI *f)(void) = (void *)Real("DllRegisterServer");
    return f ? f() : E_FAIL;
}

HRESULT WINAPI DllUnregisterServer(void)
{
    HRESULT (WINAPI *f)(void) = (void *)Real("DllUnregisterServer");
    return f ? f() : E_FAIL;
}

void *WINAPI GetdfDIJoystick(void)
{
    void *(WINAPI *f)(void) = (void *)Real("GetdfDIJoystick");
    return f ? f() : NULL;
}
