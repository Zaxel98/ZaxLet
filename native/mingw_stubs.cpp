// dllentry.cpp (BaseClasses) memanggil __security_init_cookie milik runtime MSVC (/GS). MinGW tidak memerlukannya.
extern "C" void __cdecl __security_init_cookie(void) {}
