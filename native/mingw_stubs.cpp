// dllentry.cpp (BaseClasses) calls MSVC runtime's __security_init_cookie (/GS). MinGW does not need it.
extern "C" void __cdecl __security_init_cookie(void) {}
