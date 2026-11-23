// MinGW lacks MSVC's SAL annotations (<sal.h>) used by BaseClasses; define them as empty.
#pragma once
#ifndef __in
#define __in
#endif
#define __in_opt
#define __in_ecount(x)
#define __in_bcount(x)
#define __in_ecount_opt(x)
#define __in_bcount_opt(x)
#define __inout
#define __inout_opt
#define __inout_ecount(x)
#define __inout_bcount(x)
#define __out
#define __out_opt
#define __out_ecount(x)
#define __out_bcount(x)
#define __out_ecount_opt(x)
#define __out_bcount_opt(x)
#define __out_ecount_part(x, y)
#define __out_bcount_part(x, y)
#define __deref_in
#define __deref_in_opt
#define __deref_out
#define __deref_out_opt
#define __deref_inout
#define __deref_inout_opt
#define __deref_out_ecount(x)
#define __deref_out_ecount_opt(x)
#define __deref_opt_out
#define __deref_opt_out_opt
#define __deref_opt_inout_opt
#define __deref_out_range(x, y)
#define __out_range(x, y)
#define __in_range(x, y)
#define __ecount(x)
#define __bcount(x)
#define __reserved
#define __checkReturn
#define __success(x)
#define __range(x, y)
#define __nullterminated
#define __notnull
#define __maybenull
#define __format_string
#define __control_entrypoint(x)
#define __analysis_assume(x)

// streams.h defines this only for MSVC (__declspec(novtable)); on clang it can be empty.
#ifndef AM_NOVTABLE
#define AM_NOVTABLE
#endif

// SAL annotations for struct members
#define __field_ecount(x)
#define __field_bcount(x)
#define __field_ecount_opt(x)
#define __field_bcount_opt(x)
#define __field_range(x, y)

// BaseClasses code calls min()/max() without std:: (macros in MSVC's windows.h)
#ifdef __cplusplus
#include <type_traits>
template <class A, class B> inline typename std::common_type<A, B>::type min(A a, B b) { return a < b ? a : b; }
template <class A, class B> inline typename std::common_type<A, B>::type max(A a, B b) { return a < b ? b : a; }
#endif

// Macros from Windows SDK amvideo.h missing from MinGW headers
#ifndef HEADER
#define HEADER(pVideoInfo) (&(((VIDEOINFOHEADER *)(pVideoInfo))->bmiHeader))
#endif
#ifndef SIZE_PREHEADER
#define SIZE_EGA_PALETTE (iEGA_COLORS * sizeof(RGBQUAD))
#define SIZE_PALETTE (iPALETTE_COLORS * sizeof(RGBQUAD))
#define SIZE_PREHEADER (FIELD_OFFSET(VIDEOINFOHEADER, bmiHeader))
#define SIZE_MASKS (iMASK_COLORS * sizeof(DWORD))
#define SIZE_VIDEOHEADER (sizeof(BITMAPINFOHEADER) + SIZE_PREHEADER)
#endif
#ifndef DIBSIZE
#define WIDTHBYTES(bits) ((DWORD)(((bits) + 31) & (~31)) / 8)
#define DIBWIDTHBYTES(bi) (DWORD)WIDTHBYTES((DWORD)(bi).biWidth * (DWORD)(bi).biBitCount)
#define _DIBSIZE(bi) (DIBWIDTHBYTES(bi) * (DWORD)(bi).biHeight)
#define DIBSIZE(bi) ((bi).biHeight < 0 ? (-1) * (_DIBSIZE(bi)) : _DIBSIZE(bi))
#endif

// Palette macros from Windows SDK amvideo.h
#ifndef PALETTISED
#define TRUECOLOR(pbmi) ((TRUECOLORINFO *)(((LPBYTE) & ((pbmi)->bmiHeader)) + (pbmi)->bmiHeader.biSize))
#define COLORS(pbmi) ((RGBQUAD *)(((LPBYTE) & ((pbmi)->bmiHeader)) + (pbmi)->bmiHeader.biSize))
#define BITMASKS(pbmi) ((DWORD *)(((LPBYTE) & ((pbmi)->bmiHeader)) + (pbmi)->bmiHeader.biSize))
#define PALETTISED(pbmi) ((pbmi)->bmiHeader.biBitCount <= iPALETTE)
#define PALETTE_ENTRIES(pbmi) ((DWORD)1 << (pbmi)->bmiHeader.biBitCount)
#endif

// wxutil.h declares ContainsPalette/GetBitmapPalette only if __AMVIDEO__ (Microsoft amvideo.h guard);
// MinGW's amvideo.h uses a different guard, so define it ourselves.
#ifndef __AMVIDEO__
#define __AMVIDEO__
#endif
