/* Smoke test: loads VShootingKernel.dll like Unity does and injects board lines
 * through the named pipe, checking the values VShootingGet() returns. */
#include <windows.h>
#include <stdio.h>
#include <string.h>

typedef int (*fn_i_v)(void);
typedef void (*fn_set)(int, int, int, int, int, int);
static fn_i_v pStart, pGet;
static fn_set pSet;
static int fails;

static void send_pipe(const char *msg)
{
    HANDLE h;
    DWORD w;
    for (int i = 0; i < 50; ++i) {
        h = CreateFileA("\\\\.\\pipe\\VShootingKernel", GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (h != INVALID_HANDLE_VALUE) break;
        Sleep(50);
    }
    if (h == INVALID_HANDLE_VALUE) { printf("pipe open failed\n"); fails++; return; }
    WriteFile(h, msg, (DWORD)strlen(msg), &w, NULL);
    CloseHandle(h);
}

static int wait_get(int ms)
{
    for (int t = 0; t < ms; t += 10) {
        int v = pGet();
        if (v != -1) return v;
        Sleep(10);
    }
    return -1;
}

static void expect(const char *line, int want)
{
    int got;
    send_pipe(line);
    got = wait_get(1000);
    printf("%-14.*s -> %4d (want %4d) %s\n", (int)strcspn(line, "\n"), line, got, want, got == want ? "ok" : "FAIL");
    if (got != want) fails++;
    Sleep(150); /* beyond the 100 ms hit debounce */
}

int main(int argc, char **argv)
{
    HMODULE m = LoadLibraryA("VShootingKernel.dll");
    if (m && argc > 2 && strcmp(argv[1], "--listen") == 0) {
        /* Host the DLL and print every event, like the game's linux.Update() */
        int secs = atoi(argv[2]);
        pStart = (fn_i_v)GetProcAddress(m, "VShootingStart");
        pGet = (fn_i_v)GetProcAddress(m, "VShootingGet");
        pStart();
        for (int t = 0; t < secs * 100; ++t) {
            int v = pGet();
            if (v != -1) { printf("EVENT %d\n", v); fflush(stdout); }
            Sleep(10);
        }
        return 0;
    }
    if (!m) { printf("LoadLibrary failed %lu\n", GetLastError()); return 1; }
    pStart = (fn_i_v)GetProcAddress(m, "VShootingStart");
    pGet = (fn_i_v)GetProcAddress(m, "VShootingGet");
    pSet = (fn_set)GetProcAddress(m, "VShootingSet");
    if (!pStart || !pGet || !pSet || !GetProcAddress(m, "VShootingSetAnalogThreshold") ||
        !GetProcAddress(m, "VShootingSetMissThreshold")) {
        printf("missing export\n");
        return 1;
    }
    printf("Get before start: %d (want -2)\n", pGet());
    printf("VShootingStart: %d\n", pStart());
    printf("VShootingStart again: %d (want 2)\n", pStart());
    pSet(200, 4, 33, 33, 33, 33);
    printf("Get idle: %d (want -1)\n", pGet());
    Sleep(200); /* the original drops hits within 100 ms of VShootingStart */

    expect("A1\n", 64);
    expect("E7\n", 1);    /* single 1 */
    expect("D6\n", 2);
    expect("L8\n", 16);
    expect("I8\n", 82);
    expect("M1\n", 83);
    expect("M5\n", 87);
    expect("d100\n", 100);
    expect("d55\n", 55);
    /* coin on analog ch1: rise then fall then rise, swing > threshold(350) */
    send_pipe("%a1000\n%a1600\n%a1100\n");
    expect("%a1700\n", 100);
    /* bill acceptor ch2 -> 101 + 1 pulse */
    send_pipe("%a2000\n%a2600\n%a2100\n");
    expect("%a2700\n", 102);

    printf(fails ? "\n%d FAILURES\n" : "\nALL OK\n", fails);
    return fails ? 1 : 0;
}
