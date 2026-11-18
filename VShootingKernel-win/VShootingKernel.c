/*
 * VShootingKernel.dll - Windows reimplementation of libVShootingKernel.so
 *
 * Reverse engineered from the Linux x86_64 build of libVShootingKernel.so
 * (build date string "Jul 18 2019"). The game (Assembly-CSharp.dll, class `linux`)
 * P/Invokes: VShootingStart, VShootingGet, VShootingSet,
 *            VShootingSetAnalogThreshold, VShootingSetMissThreshold.
 * All other original exports that have a meaning on Windows are reproduced too.
 * The V4L2 camera functions (VShootingCam*) are not used by the game and are not
 * implemented.
 *
 * Hardware link: the dartboard controller is a USB CDC/serial device
 * (Linux: /dev/ttyACM0, /dev/ttyUSB0, ...), 115200 8N1, raw mode, line based.
 *
 *   Board -> PC (one line, terminated by CR or LF):
 *     "<A..L><1..8>"   segment hit; mapped through VShootingTable (1..82)
 *     "M<1..5>"        buttons; mapped to 83..87
 *     "%a<ch><value>"  analog channel <ch>='0'..'8' sample (miss sensor, coin,
 *                      bill acceptor, extra inputs)
 *   PC -> Board: raw 4 byte commands queued with VShootingSet (lamps etc.).
 *
 *   VShootingGet() return values (consumed by linux.Update in C#):
 *     -2  kernel not started      -1  nothing pending
 *      0  miss (dart hit the cabinet, detected by analog channel 0)
 *      1..82 board segment        83..87 buttons
 *     90..94 analog channels 4..8 100 coin   101+n  n bill pulses
 *
 * Windows replacements for Linux-only facilities:
 *   - serial port:  COM port from VSHOOTING_PORT env var, or VShootingKernel.ini
 *                   ("port=COM5"), otherwise every port listed in
 *                   HKLM\HARDWARE\DEVICEMAP\SERIALCOMM is tried in order.
 *   - /dev/shm/VShootingKernel.fifo -> named pipe \\.\pipe\VShootingKernel.
 *       "d<N>"      injects event N directly (same as the original fifo)
 *       any other   line is parsed exactly as if it came from the board
 *                   (e.g. "A1", "M2", "%a0300"), handy for testing w/o hardware
 *   - log file /opt/vdarts/log/ttyACM.log -> VShootingKernel.log next to the
 *       DLL, only if that file already exists (same "log only if present"
 *       behaviour as the original), or VSHOOTING_LOG=<path>.
 *   - /opt/vdarts/vdarts-u3d-reset.sh (run when the board spams A3) is only logged.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <process.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define EXPORT __declspec(dllexport)

/* ------------------------------------------------------------------ state */

/* Initial values taken from .data of the original */
static volatile LONG pthreadState = 1;   /* 1 = idle, 2 = running, 0 = stopping */
static int  s_Threshold      = 350;
static int  s_MissThreshold  = 40;
static int  s_MissDelay      = 300;
static int  s_MaxMissDiff    = 0;
static unsigned int s_msecMissGet = 0;
static unsigned int s_msecDartGet = 0;
static int  VShootingDebugMode = 0;

static int  VShootingGetData[5];
static int  VShootingGetFlag = -1;
static int  VShootingSetData[30 * 11];
static int  VShootingSetFlag = -1;
static int  VShootingTable[104];

static CRITICAL_SECTION mtxGet, mtxSet, mtxParse;
static HANDLE hDev = INVALID_HANDLE_VALUE;
static HANDLE thGet, thSet, thFifo;
static LARGE_INTEGER qpcFreq, qpcStart;

static FILE *s_logFile;
static char  s_logPath[MAX_PATH];
static char  s_dllDir[MAX_PATH];
static char  s_portUsed[64];

/* ------------------------------------------------------------- utilities */

/* Milliseconds since VShootingStart (original: gettimeofday - startTime). */
static unsigned int now_ms(void)
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return (unsigned int)((t.QuadPart - qpcStart.QuadPart) * 1000 / qpcFreq.QuadPart);
}

static double now_sec(void)
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return (double)(t.QuadPart - qpcStart.QuadPart) / (double)qpcFreq.QuadPart;
}

static void logf_(const char *fmt, ...)
{
    va_list ap;
    FILE *f = s_logFile ? s_logFile : stderr;
    va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);
    fflush(f);
}

static void VShootingTableInit(void)
{
    static const int t[104] = {
        64, -1, 44, 33, 53,  4, 24, 13,   /* A1..A8 */
        67, -1, 79, 62, 63, -1, 77, 81,   /* B */
        78, -1, 58, 18, 38, 26, 46,  6,   /* C */
        69, -1, 49, 42, 29,  2,  9, 22,   /* D */
        61, -1, 41, 50, 21, 30,  1, 10,   /* E */
        74, -1, 54, 57, 34, 17, 14, 37,   /* F */
        80, -1, 60, 55, 40, 35, 20, 15,   /* G */
        71, -1, 51, 43, 31,  3, 11, 23,   /* H */
        65, -1, 73, 75, 66,  3, 70, 82,   /* I */
        68, -1, 48,  8, 28, 19, 59, 39,   /* J */
        72, -1, 52, 25, 45,  5, 32, 12,   /* K */
        76, -1, 56, 27, 47,  7, 36, 16,   /* L */
        83, 84, 85, 86, 87, -1, -1, -1    /* M1..M5 */
    };
    memcpy(VShootingTable, t, sizeof t);
}

static void push_event(int v)
{
    EnterCriticalSection(&mtxGet);
    if (VShootingGetFlag == 4) {
        for (int k = 0; k <= 3; ++k) VShootingGetData[k] = VShootingGetData[k + 1];
        --VShootingGetFlag;
    }
    VShootingGetData[++VShootingGetFlag] = v;
    LeaveCriticalSection(&mtxGet);
}

/* ----------------------------------------------------------- serial port */

static void close_dev(void)
{
    if (hDev != INVALID_HANDLE_VALUE) {
        CloseHandle(hDev);
        hDev = INVALID_HANDLE_VALUE;
    }
}

static HANDLE open_com(const char *name)
{
    char path[80];
    HANDLE h;
    DCB dcb;
    COMMTIMEOUTS to;

    if (_strnicmp(name, "\\\\.\\", 4) == 0) snprintf(path, sizeof path, "%s", name);
    else snprintf(path, sizeof path, "\\\\.\\%s", name);

    h = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED, NULL);
    if (h == INVALID_HANDLE_VALUE) return h;

    memset(&dcb, 0, sizeof dcb);
    dcb.DCBlength = sizeof dcb;
    GetCommState(h, &dcb);
    dcb.BaudRate = CBR_115200;          /* cfsetispeed(B115200) */
    dcb.ByteSize = 8;                   /* CS8 */
    dcb.Parity = NOPARITY;              /* ~PARENB */
    dcb.StopBits = ONESTOPBIT;          /* ~CSTOPB */
    dcb.fBinary = TRUE;
    dcb.fParity = FALSE;
    dcb.fOutxCtsFlow = FALSE;
    dcb.fOutxDsrFlow = FALSE;
    dcb.fDsrSensitivity = FALSE;
    dcb.fOutX = FALSE;
    dcb.fInX = FALSE;
    dcb.fErrorChar = FALSE;
    dcb.fNull = FALSE;
    dcb.fAbortOnError = FALSE;
    dcb.fDtrControl = DTR_CONTROL_ENABLE;   /* Linux asserts DTR/RTS on open */
    dcb.fRtsControl = RTS_CONTROL_ENABLE;
    if (!SetCommState(h, &dcb)) {
        CloseHandle(h);
        return INVALID_HANDLE_VALUE;
    }
    /* VMIN=0 VTIME=10: a read returns after 1 s without data */
    memset(&to, 0, sizeof to);
    to.ReadTotalTimeoutConstant = 1000;
    to.WriteTotalTimeoutConstant = 1000;
    SetCommTimeouts(h, &to);
    PurgeComm(h, PURGE_RXCLEAR | PURGE_RXABORT);   /* tcflush(TCIFLUSH) */
    return h;
}

static int try_port(const char *name)
{
    logf_("# VShootingStart try %s  \n", name);
    hDev = open_com(name);
    if (hDev != INVALID_HANDLE_VALUE) {
        snprintf(s_portUsed, sizeof s_portUsed, "%s", name);
        return 1;
    }
    return 0;
}

static void trim(char *s)
{
    char *p = s, *e;
    while (*p == ' ' || *p == '\t') ++p;
    memmove(s, p, strlen(p) + 1);
    e = s + strlen(s);
    while (e > s && (e[-1] == ' ' || e[-1] == '\t' || e[-1] == '\r' || e[-1] == '\n')) *--e = 0;
}

/* Configured port: env var VSHOOTING_PORT, else "port=" in VShootingKernel.ini */
static int configured_port(char *out, size_t n)
{
    char ini[MAX_PATH], line[256];
    const char *env = getenv("VSHOOTING_PORT");
    FILE *f;

    if (env && *env) {
        snprintf(out, n, "%s", env);
        return 1;
    }
    snprintf(ini, sizeof ini, "%sVShootingKernel.ini", s_dllDir);
    f = fopen(ini, "r");
    if (!f) return 0;
    while (fgets(line, sizeof line, f)) {
        trim(line);
        if (_strnicmp(line, "port", 4) == 0) {
            char *eq = strchr(line, '=');
            if (eq) {
                ++eq;
                trim(eq);
                if (*eq && _stricmp(eq, "auto") != 0) {
                    snprintf(out, n, "%s", eq);
                    fclose(f);
                    return 1;
                }
            }
        }
    }
    fclose(f);
    return 0;
}

static int open_device(void)
{
    char port[64];
    HKEY key;

    if (configured_port(port, sizeof port))
        return try_port(port);

    /* Auto: every serial port present on the system */
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, "HARDWARE\\DEVICEMAP\\SERIALCOMM", 0,
                      KEY_READ, &key) == ERROR_SUCCESS) {
        for (DWORD i = 0;; ++i) {
            char name[256], data[64];
            DWORD nlen = sizeof name, dlen = sizeof data - 1, type;
            LONG r = RegEnumValueA(key, i, name, &nlen, NULL, &type, (BYTE *)data, &dlen);
            if (r == ERROR_NO_MORE_ITEMS) break;
            if (r != ERROR_SUCCESS || type != REG_SZ) continue;
            data[dlen] = 0;
            if (try_port(data)) {
                RegCloseKey(key);
                return 1;
            }
        }
        RegCloseKey(key);
    }
    return 0;
}

/* Overlapped read of one byte with the port's 1 s timeout.
 * Returns 1 = got a byte, 0 = timeout, -1 = error. */
static int dev_read_byte(char *c)
{
    OVERLAPPED ov;
    DWORD got = 0;
    int ret;

    if (hDev == INVALID_HANDLE_VALUE) return -1;
    memset(&ov, 0, sizeof ov);
    ov.hEvent = CreateEventA(NULL, TRUE, FALSE, NULL);
    if (!ReadFile(hDev, c, 1, &got, &ov)) {
        if (GetLastError() != ERROR_IO_PENDING) {
            CloseHandle(ov.hEvent);
            return -1;
        }
        if (!GetOverlappedResult(hDev, &ov, &got, TRUE)) {
            CloseHandle(ov.hEvent);
            return -1;
        }
    }
    CloseHandle(ov.hEvent);
    ret = got == 1 ? 1 : 0;
    return ret;
}

static int dev_write(const void *buf, DWORD len)
{
    OVERLAPPED ov;
    DWORD put = 0;

    if (hDev == INVALID_HANDLE_VALUE) return -1;
    memset(&ov, 0, sizeof ov);
    ov.hEvent = CreateEventA(NULL, TRUE, FALSE, NULL);
    if (!WriteFile(hDev, buf, len, &put, &ov)) {
        if (GetLastError() != ERROR_IO_PENDING || !GetOverlappedResult(hDev, &ov, &put, TRUE)) {
            CloseHandle(ov.hEvent);
            return -1;
        }
    }
    CloseHandle(ov.hEvent);
    return (int)put;
}

/* tty_read(): collect bytes until CR/LF; empty lines are skipped, NULs dropped.
 * Returns line length (terminator normalised to '\n') or 0/-1 on timeout/error. */
static int tty_read(char *buf, int cap)
{
    int n = 0;
    char c;
    for (;;) {
        int r = dev_read_byte(&c);
        if (r <= 0) return r;
        if (!c) continue;
        if (n < cap - 1) buf[n++] = c;
        if (c == '\r' || c == '\n') {
            if (n > 1) {
                buf[n - 1] = '\n';
                return n;
            }
            n = 0;
        }
    }
}

/* -------------------------------------------------------- line protocol */

/* Parser state that lived on pthread_get's stack in the original. */
static struct {
    int lastVal[16];   /* v8: last sample, ch0 starts at 512 */
    int accum[16];     /* v7: accumulated swing in current direction */
    int dir[16];       /* v6: 1 = rising, 2 = falling */
    int counter[9];    /* v9: pulse counters (ch0 unused, ch2 = bill) */
    int a3Spam;        /* v33 */
} P;

static void parser_reset(void)
{
    memset(&P, 0, sizeof P);
    P.lastVal[0] = 512;
}

/* Parse one line. Returns event value or -1. Must hold mtxParse. */
static int parse_line(const unsigned char *s, int len)
{
    int ev = -1;
    (void)len;

    if (s[0] == '%' && s[1] == 'a') {
        int ch = s[2] - '0';
        int last, acc, olddir, val, diff, newdir, thr, fire = 0;
        if (ch < 0 || ch > 15) return -1;   /* original had no bounds check */
        last = P.lastVal[ch];
        acc = P.accum[ch];
        olddir = P.dir[ch];
        val = atoi((const char *)s + 3);
        diff = val - last;
        thr = s_Threshold;
        newdir = val < last ? 2 : 1;
        if (P.dir[ch] == newdir) {
            P.accum[ch] += abs(diff);
        } else {
            if (ch == 0) {
                thr = s_MissThreshold;
                if (abs(acc) > abs(s_MaxMissDiff)) s_MaxMissDiff = acc;
            }
            if (abs(acc) <= thr) {
                logf_("#  Analog %d Signal FAIL %d %d\n", ch, abs(acc), thr);
            } else {
                logf_("#  Analog %d Signal OK %d %d\n", ch, abs(acc), thr);
                if (ch) {
                    if (olddir == 2) fire = 1;
                } else {
                    s_msecMissGet = now_ms();
                }
            }
            P.accum[ch] = abs(diff);
            P.dir[ch] = newdir;
        }
        P.lastVal[ch] = val;
        if (fire) {
            switch (ch) {
            case 0: case 2: ++P.counter[ch]; break;
            case 1: ev = 100; break;   /* coin */
            case 4: ev = 90; break;
            case 5: ev = 91; break;
            case 6: ev = 92; break;
            case 7: ev = 93; break;
            case 8: ev = 94; break;
            default: break;
            }
        }
    } else if (s[0] == 'M') {
        int idx = s[1] + 47;
        ev = (s[1] >= '1' && idx < 104) ? VShootingTable[idx] : -1;
    } else if (s[0] < 'A' || s[0] > 'L') {
        ev = -1;
    } else {
        unsigned int prev = s_msecDartGet;
        s_msecDartGet = now_ms();
        if (prev + 100 >= s_msecDartGet) {
            s_msecDartGet = prev;           /* debounce: second hit within 100 ms */
            logf_("## HIT --- : %d  %d \n", prev, ev);
        } else {
            int idx = 8 * s[0] - 569 + s[1];
            ev = (idx >= 0 && idx < 96) ? VShootingTable[idx] : -1;
            logf_("## HIT +++ : %d  %d \n", s_msecDartGet, ev);
        }
        if (prev + 2000 > s_msecDartGet && s[0] == 'A' && s[1] == '3') {
            if (++P.a3Spam > 10)
                logf_("## board reset requested (A3 spam) - /opt/vdarts/vdarts-u3d-reset.sh not run on Windows\n");
        } else {
            P.a3Spam = 0;
        }
    }

    /* Deferred counters (clock vars were never armed in the original, so these
     * flush immediately on the same pass). */
    if (P.counter[0] > 0 && ev == -1) {
        ev = 0;
        P.counter[0] = 0;
    }
    if (P.counter[2] && ev == -1) {
        ev = P.counter[2] + 101;   /* bill acceptor pulses */
        P.counter[2] = 0;
    }
    return ev;
}

static void handle_line(const char *line, int len)
{
    int ev;
    EnterCriticalSection(&mtxParse);
    ev = parse_line((const unsigned char *)line, len);
    LeaveCriticalSection(&mtxParse);
    if (ev != -1) push_event(ev);
}

/* --------------------------------------------------------------- threads */

static int VShootingStartInternal(int reconnect);

static unsigned __stdcall pthread_get(void *arg)
{
    char buf[128];
    int idle = 0;
    time_t lastStamp = 0;
    (void)arg;

    while (pthreadState) {
        int n;
        if (hDev == INVALID_HANDLE_VALUE) {
            /* No board: retry every 2 s (original: reconnect after read errors) */
            for (int i = 0; i < 20 && pthreadState; ++i) Sleep(100);
            if (!pthreadState) break;
            VShootingStartInternal(1);
            continue;
        }
        memset(buf, 0, sizeof buf);
        n = tty_read(buf, sizeof buf);
        if (n < 0) {
            if (!pthreadState) break;
            logf_("####  read error on %s (%lu), reconnecting\n", s_portUsed, GetLastError());
            close_dev();
            EnterCriticalSection(&mtxSet);
            VShootingSetFlag = -1;
            LeaveCriticalSection(&mtxSet);
            continue;
        }
        if (n == 0) {
            if (++idle > 60000) {   /* same as original: ~16 h without data */
                logf_("####  read timeout on %s, reconnecting\n", s_portUsed);
                close_dev();
                idle = 0;
            }
            continue;
        }
        idle = 0;
        {
            time_t t = time(NULL);
            if (t - lastStamp > 60) {
                logf_("####  time %s", ctime(&t));
                lastStamp = t;
            }
        }
        if (s_logFile) {
            fprintf(s_logFile, "## GET <-- : %u  ", now_ms());
            for (int j = 0; j < n; ++j) {
                unsigned char c = (unsigned char)buf[j];
                if ((c > 0x1F && c <= 0x7E) || c == '\n' || c == '\r') fputc(c, s_logFile);
                else fprintf(s_logFile, "(%d)", c);
            }
            fflush(s_logFile);
        }
        handle_line(buf, n);
    }
    return 0;
}

static unsigned __stdcall pthread_set(void *arg)
{
    double last = 0.0;
    (void)arg;

    while (pthreadState) {
        double now;
        Sleep(5);
        now = now_sec();
        if (now - last <= 0.01) continue;

        EnterCriticalSection(&mtxSet);
        if (VShootingSetFlag != -1 && now - last > (double)VShootingSetData[0] / 1000.0) {
            unsigned char b[9];
            int len = VShootingSetData[1], w;
            for (int i = 0; i <= 8; ++i) b[i] = (unsigned char)VShootingSetData[i + 2];
            if (s_logFile) {
                fputs("## --> SET : ", s_logFile);
                for (int j = 0; j < len; ++j) {
                    if (b[j] <= 0x1F || b[j] > 0x7E) fprintf(s_logFile, "(%d)", b[j]);
                    else fputc(b[j], s_logFile);
                }
                fputs(" \n", s_logFile);
                fflush(s_logFile);
            }
            if (hDev != INVALID_HANDLE_VALUE) {   /* no board: drop silently */
                w = dev_write(b, (DWORD)len);
                if (w != len) logf_("## write num %d < %d \n", w, len);
            }
            for (int i = 0; i < VShootingSetFlag; ++i)
                memmove(&VShootingSetData[11 * i], &VShootingSetData[11 * i + 11], 11 * sizeof(int));
            --VShootingSetFlag;
            last = now_sec();
        } else if (hDev == INVALID_HANDLE_VALUE) {
            last = 0.0;
        }
        LeaveCriticalSection(&mtxSet);
    }
    return 0;
}

/* Replacement for the /dev/shm fifo: named pipe, one message/line per write. */
static unsigned __stdcall pthread_fifo(void *arg)
{
    (void)arg;
    while (pthreadState) {
        HANDLE pipe = CreateNamedPipeA("\\\\.\\pipe\\VShootingKernel",
                                       PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED,
                                       PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                                       1, 0, 4096, 0, NULL);
        OVERLAPPED ov;
        char buf[256];
        int have = 0;

        if (pipe == INVALID_HANDLE_VALUE) {
            logf_("## err open fifo %lu\n", GetLastError());
            return 0;
        }
        memset(&ov, 0, sizeof ov);
        ov.hEvent = CreateEventA(NULL, TRUE, FALSE, NULL);
        if (!ConnectNamedPipe(pipe, &ov)) {
            DWORD e = GetLastError();
            if (e == ERROR_IO_PENDING) {
                while (pthreadState && WaitForSingleObject(ov.hEvent, 200) == WAIT_TIMEOUT) {}
                if (!pthreadState) {
                    CancelIo(pipe);
                    CloseHandle(ov.hEvent);
                    CloseHandle(pipe);
                    break;
                }
            } else if (e != ERROR_PIPE_CONNECTED) {
                CloseHandle(ov.hEvent);
                CloseHandle(pipe);
                Sleep(200);
                continue;
            }
        }
        /* Read until the client disconnects */
        while (pthreadState) {
            DWORD got = 0;
            ResetEvent(ov.hEvent);
            if (!ReadFile(pipe, buf + have, (DWORD)(sizeof buf - 1 - have), &got, &ov)) {
                if (GetLastError() != ERROR_IO_PENDING) break;
                while (pthreadState && WaitForSingleObject(ov.hEvent, 200) == WAIT_TIMEOUT) {}
                if (!pthreadState) { CancelIo(pipe); break; }
                if (!GetOverlappedResult(pipe, &ov, &got, FALSE)) break;
            }
            if (got == 0) continue;
            have += (int)got;
            buf[have] = 0;
            for (;;) {
                char *nl = strpbrk(buf, "\r\n");
                char line[260];
                int ll;
                if (!nl) {
                    if (have >= (int)sizeof buf - 1) { nl = buf + have; } else break;
                }
                ll = (int)(nl - buf);
                memcpy(line, buf, ll);
                line[ll] = 0;
                if (*nl) ++nl;
                have -= (int)(nl - buf);
                memmove(buf, nl, have + 1);
                if (ll == 0) continue;
                if (line[0] == 'd' && ll > 1) {
                    unsigned int v = (unsigned int)atoi(line + 1);
                    if (v <= 101) push_event((int)v);     /* original limit */
                    else logf_("# read fifo : %s (out of range)\n", line);
                } else {
                    logf_("# read fifo : %s\n", line);
                    line[ll] = '\n';
                    handle_line(line, ll + 1);
                }
            }
        }
        /* Process a final line that had no terminator */
        if (have > 0) {
            buf[have] = 0;
            if (buf[0] == 'd') {
                unsigned int v = (unsigned int)atoi(buf + 1);
                if (v <= 101) push_event((int)v);
            } else {
                handle_line(buf, have);
            }
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(ov.hEvent);
        CloseHandle(pipe);
    }
    return 0;
}

/* --------------------------------------------------------------- exports */

EXPORT void VShootingSet(int delayMs, int count, int b0, int b1, int b2, int b3)
{
    if (count > 9 || (unsigned)b0 >= 0x100 || (unsigned)b1 >= 0x100 ||
        (unsigned)b2 >= 0x100 || (unsigned)b3 >= 0x100)
        return;
    EnterCriticalSection(&mtxSet);
    if (VShootingSetFlag == 29) {
        for (int i = 0; i <= 28; ++i)
            memmove(&VShootingSetData[11 * i], &VShootingSetData[11 * i + 11], 11 * sizeof(int));
        --VShootingSetFlag;
    }
    ++VShootingSetFlag;
    VShootingSetData[11 * VShootingSetFlag + 0] = delayMs;
    VShootingSetData[11 * VShootingSetFlag + 1] = count;
    VShootingSetData[11 * VShootingSetFlag + 2] = b0;
    VShootingSetData[11 * VShootingSetFlag + 3] = b1;
    VShootingSetData[11 * VShootingSetFlag + 4] = b2;
    VShootingSetData[11 * VShootingSetFlag + 5] = b3;
    LeaveCriticalSection(&mtxSet);
}

static void open_log(void)
{
    const char *env = getenv("VSHOOTING_LOG");
    if (s_logFile && s_logFile != stderr) return;
    if (env && *env) snprintf(s_logPath, sizeof s_logPath, "%s", env);
    else snprintf(s_logPath, sizeof s_logPath, "%sVShootingKernel.log", s_dllDir);
    /* Original: log only when the file already exists, else stderr */
    if ((env && *env) || GetFileAttributesA(s_logPath) != INVALID_FILE_ATTRIBUTES)
        s_logFile = fopen(s_logPath, "wb");
}

/* Port open + board init sequence (body of the original VShootingStart). */
static int VShootingStartInternal(int reconnect)
{
    int ok;

    ok = open_device();
    if (ok) {
        logf_("# VShootingStart opened %s\n", s_portUsed);
        EnterCriticalSection(&mtxGet);
        VShootingGetFlag = -1;
        memset(VShootingGetData, 0, sizeof VShootingGetData);
        LeaveCriticalSection(&mtxGet);
        EnterCriticalSection(&mtxSet);
        VShootingSetFlag = -1;
        memset(VShootingSetData, 0, sizeof VShootingSetData);
        LeaveCriticalSection(&mtxSet);
    }
    if (!ok) return -1;
    if (reconnect) {
        EnterCriticalSection(&mtxParse);
        parser_reset();
        LeaveCriticalSection(&mtxParse);
    }

    /* Board initialisation sequence, verbatim from the original */
    VShootingSet(3000, 4, 32, 32, 32, 32);
    VShootingSet(1000, 4, 33, 33, 33, 33);
    VShootingSet(1000, 4, 37, 97, 0, 20);
    VShootingSet(1000, 4, 37, 97, 10, 5);
    VShootingSet(1000, 4, 37, 97, 1, 60);
    VShootingSet(1000, 4, 37, 97, 11, 5);
    VShootingSet(1000, 4, 37, 97, 2, 60);
    VShootingSet(1000, 4, 37, 97, 12, 5);
    VShootingSet(1000, 4, 37, 97, 5, 100);
    VShootingSet(1000, 4, 37, 97, 6, 100);
    VShootingSet(1000, 4, 37, 97, 7, 100);
    VShootingSet(1000, 4, 37, 97, 8, 100);
    VShootingSet(1000, 4, 33, 33, 33, 33);
    return 1;
}

EXPORT int VShootingStart(void)
{
    int r;

    if (pthreadState == 2) return 2;
    open_log();
    logf_("# VShootingStart %s ? %s (Windows port)\n", "Jul 18 2019", s_logPath);
    QueryPerformanceCounter(&qpcStart);
    VShootingTableInit();
    parser_reset();

    r = VShootingStartInternal(0);
    if (r != 1) logf_("# VShootingStart: no board found, waiting for it (pipe input active)\n");

    /* Unlike the original we always start the threads, so the named pipe works
     * without hardware and a board plugged in later is picked up. */
    if (pthreadState == 1) {
        pthreadState = 2;
        thGet  = (HANDLE)_beginthreadex(NULL, 0, pthread_get, NULL, 0, NULL);
        thSet  = (HANDLE)_beginthreadex(NULL, 0, pthread_set, NULL, 0, NULL);
        thFifo = (HANDLE)_beginthreadex(NULL, 0, pthread_fifo, NULL, 0, NULL);
    }
    logf_("# VShootingStart OK  \n");
    return r;
}

EXPORT int VShootingGet(void)
{
    int v = -1;
    if (pthreadState != 2) return -2;

    EnterCriticalSection(&mtxGet);
    if (s_msecMissGet) {
        unsigned int now = now_ms();
        if ((unsigned)s_MissDelay + s_msecMissGet < now) {
            if ((!s_msecDartGet || (unsigned)(4 * s_MissDelay) + s_msecDartGet + 600 < s_msecMissGet)
                && s_msecMissGet + 2000 > now) {
                v = 0;
                logf_("## MISS --> : %d  %d  %d  %d \n", s_msecDartGet, s_msecMissGet,
                      s_msecMissGet - s_msecDartGet, s_MissDelay);
            }
            s_msecDartGet = 0;
            s_msecMissGet = 0;
        }
    }
    if (VShootingGetFlag != -1 && v == -1) {
        v = VShootingGetData[0];
        logf_("## GetD --> : %d \n", v);
        for (int i = 0; i < VShootingGetFlag; ++i) VShootingGetData[i] = VShootingGetData[i + 1];
        --VShootingGetFlag;
    }
    LeaveCriticalSection(&mtxGet);
    return v;
}

EXPORT int VShootingGetClear(void)
{
    if (pthreadState != 2) return -2;
    EnterCriticalSection(&mtxGet);
    VShootingGetFlag = -1;
    LeaveCriticalSection(&mtxGet);
    return 1;
}

EXPORT void VShootingSetAnalogThreshold(int val)
{
    if (val > 10 && val <= 899) s_Threshold = val;
}

EXPORT int VShootingSetMissThreshold(int val)
{
    int prev;
    if (val > 10 && val <= 899) {
        s_MissThreshold = val;
        if (val > 30) s_MissDelay = s_MissThreshold > 60 ? 300 : 200;
        else s_MissDelay = 100;
    }
    prev = s_MaxMissDiff;
    s_MaxMissDiff = 0;
    return prev;
}

EXPORT int VShootingSetMissDelay(int val)
{
    /* sic: the original writes s_MissThreshold here */
    if (val > 10 && val <= 1899) s_MissThreshold = val;
    return s_MissThreshold;
}

EXPORT int VShootingSetTarget(unsigned a, unsigned b, unsigned c, unsigned d)
{
    if (a >= 2) a = 0;
    if (b >= 2) b = 0;
    if (c >= 2) c = 0;
    if (d >= 2) d = 0;
    VShootingSet(0, 4, 34, (int)(4 * (2 * (2 * (2 * a + b) + c) + d)), 0, 0);
    return 0;
}

EXPORT void VShootingInit(void) { VShootingTableInit(); }

EXPORT void VShootingDebug(void) { VShootingDebugMode = 1; }

EXPORT void VShootingStop(void)
{
    if (pthreadState == 2) pthreadState = 0;
    if (hDev != INVALID_HANDLE_VALUE) CancelIoEx(hDev, NULL);
    if (thGet)  { WaitForSingleObject(thGet, 3000);  CloseHandle(thGet);  thGet = NULL; }
    if (thSet)  { WaitForSingleObject(thSet, 3000);  CloseHandle(thSet);  thSet = NULL; }
    if (thFifo) { WaitForSingleObject(thFifo, 3000); CloseHandle(thFifo); thFifo = NULL; }
    close_dev();
    pthreadState = 1;
    if (s_logFile) { fclose(s_logFile); s_logFile = NULL; }
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        char *slash;
        DisableThreadLibraryCalls(inst);
        InitializeCriticalSection(&mtxGet);
        InitializeCriticalSection(&mtxSet);
        InitializeCriticalSection(&mtxParse);
        QueryPerformanceFrequency(&qpcFreq);
        QueryPerformanceCounter(&qpcStart);
        GetModuleFileNameA(inst, s_dllDir, sizeof s_dllDir);
        slash = strrchr(s_dllDir, '\\');
        if (slash) slash[1] = 0; else s_dllDir[0] = 0;
        VShootingTableInit();
        parser_reset();
    }
    return TRUE;
}
