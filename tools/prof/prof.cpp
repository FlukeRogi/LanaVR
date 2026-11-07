// Minimal sampling profiler: suspends the busiest thread of a process, walks its stack and reports
// where the time goes. Usage: prof <processName.exe> <seconds> <symbolSearchPath>
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <algorithm>
#include <cstdio>
#include <map>
#include <set>
#include <string>
#include <vector>
#pragma comment(lib, "dbghelp.lib")

static DWORD FindProcess(const wchar_t* name) {
    HANDLE s = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W e{sizeof e};
    DWORD pid = 0;
    for (BOOL ok = Process32FirstW(s, &e); ok; ok = Process32NextW(s, &e))
        if (!_wcsicmp(e.szExeFile, name)) pid = e.th32ProcessID;
    CloseHandle(s);
    return pid;
}
static ULONGLONG CpuTime(HANDLE t) {
    FILETIME a, b, k, u;
    if (!GetThreadTimes(t, &a, &b, &k, &u)) return 0;
    return ((ULONGLONG)k.dwHighDateTime << 32 | k.dwLowDateTime) + ((ULONGLONG)u.dwHighDateTime << 32 | u.dwLowDateTime);
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 4) return 1;
    DWORD pid = FindProcess(argv[1]);
    if (!pid) { puts("process not found"); return 1; }
    int seconds = _wtoi(argv[2]);
    HANDLE proc = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
    char search[2048];
    WideCharToMultiByte(CP_ACP, 0, argv[3], -1, search, sizeof search, nullptr, nullptr);
    SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_FAIL_CRITICAL_ERRORS);
    SymInitialize(proc, search, TRUE);

    std::vector<std::pair<DWORD, HANDLE>> threads;
    HANDLE s = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    THREADENTRY32 te{sizeof te};
    for (BOOL ok = Thread32First(s, &te); ok; ok = Thread32Next(s, &te))
        if (te.th32OwnerProcessID == pid)
            if (HANDLE h = OpenThread(THREAD_ALL_ACCESS, FALSE, te.th32ThreadID)) threads.push_back({te.th32ThreadID, h});
    CloseHandle(s);

    std::map<DWORD, ULONGLONG> before;
    for (auto& t : threads) before[t.first] = CpuTime(t.second);
    Sleep(1000);
    HANDLE target = nullptr;
    ULONGLONG best = 0;
    for (auto& t : threads) {
        ULONGLONG d = CpuTime(t.second) - before[t.first];
        if (d > best) { best = d; target = t.second; }
    }
    printf("busiest thread used %.0f%% of a core\n", best / 100000.0);

    std::map<DWORD64, std::string> names;
    auto Name = [&](DWORD64 a) -> std::string {
        auto it = names.find(a);
        if (it != names.end()) return it->second;
        char buf[sizeof(SYMBOL_INFO) + 512];
        auto* si = (SYMBOL_INFO*)buf;
        si->SizeOfStruct = sizeof(SYMBOL_INFO);
        si->MaxNameLen = 512;
        DWORD64 disp = 0;
        std::string r;
        IMAGEHLP_MODULE64 m{sizeof m};
        std::string mod = SymGetModuleInfo64(proc, a, &m) ? m.ModuleName : "?";
        if (SymFromAddr(proc, a, &disp, si)) r = mod + "!" + si->Name;
        else { char b[64]; sprintf_s(b, "+0x%llx", a - m.BaseOfImage); r = mod + b; }
        names[a] = r;
        return r;
    };

    std::map<std::string, int> self, incl;
    int samples = 0;
    DWORD end = GetTickCount() + seconds * 1000;
    while (GetTickCount() < end) {
        if (SuspendThread(target) == (DWORD)-1) break;
        CONTEXT c{};
        c.ContextFlags = CONTEXT_FULL;
        if (GetThreadContext(target, &c)) {
            STACKFRAME64 f{};
            f.AddrPC.Offset = c.Rip; f.AddrPC.Mode = AddrModeFlat;
            f.AddrFrame.Offset = c.Rbp; f.AddrFrame.Mode = AddrModeFlat;
            f.AddrStack.Offset = c.Rsp; f.AddrStack.Mode = AddrModeFlat;
            std::set<std::string> seen;
            bool first = true;
            for (int d = 0; d < 80; d++) {
                if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, proc, target, &f, &c, nullptr, SymFunctionTableAccess64, SymGetModuleBase64, nullptr) || !f.AddrPC.Offset) break;
                auto n = Name(f.AddrPC.Offset);
                // Symbol lookups can touch a lock the suspended thread holds, so the thread resumes first.
                if (first) { self[n]++; first = false; }
                if (seen.insert(n).second) incl[n]++;
            }
            samples++;
        }
        ResumeThread(target);
        Sleep(4);
    }
    printf("samples=%d\n--- modules\n", samples);
    std::vector<std::pair<std::string, int>> v(self.begin(), self.end());
    std::sort(v.begin(), v.end(), [](auto& a, auto& b) { return a.second > b.second; });
    puts("--- self");
    for (size_t i = 0; i < v.size() && i < 40; i++) printf("%5.1f%% %s\n", 100.0 * v[i].second / samples, v[i].first.c_str());
    v.assign(incl.begin(), incl.end());
    std::sort(v.begin(), v.end(), [](auto& a, auto& b) { return a.second > b.second; });
    puts("--- inclusive");
    for (size_t i = 0; i < v.size() && i < 90; i++) printf("%5.1f%% %s\n", 100.0 * v[i].second / samples, v[i].first.c_str());

    IMAGEHLP_MODULE64 m{sizeof m};
    for (auto& mod : {"UnityPlayer", "GameAssembly"}) {
        DWORD64 base = SymGetModuleBase64(proc, 0);
        (void)base; (void)mod;
    }
    return 0;
}
