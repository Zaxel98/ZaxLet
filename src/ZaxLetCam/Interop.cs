using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ZaxLetCam;

public static class K_Test { public static readonly Guid Sub = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"); }
static class K
{
    public const string Clsid = "6F1B3C20-4B7E-4C58-9D0A-15A4D3C0E501";
    public const int Width = 1280, Height = 720, Fps = 30, FrameBytes = Width * Height * 3 / 2;
    public const string Dir = @"C:\ProgramData\ZaxLetCam", FramePath = Dir + @"\frame.bin";

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        Par = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        AllIndep = new("c9173739-5e56-461c-b713-46fb995cb95f"), Stride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),
        SampleSize = new("dad3ab78-1990-408b-bce2-eba673dacc10"), FixedSize = new("b8ebefaf-b718-4e04-b0a9-116775e3321b"),
        AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Video = new("73646976-0000-0010-8000-00aa00389b71"),
        NV12 = new("3231564e-0000-0010-8000-00aa00389b71"),
        StreamCategory = new("2939e7b8-a62e-4579-b674-d4073dfabbba"), StreamId = new("11bd5120-d124-446b-88e6-17060257fff9"),
        FsShared = new("1cb378e9-b279-41d4-af97-34a243e68320"), PinVideoCapture = new("fb6c4281-0353-11d1-905f-0000c0cc16ba"),
        Token = new("8294da66-f328-4805-b551-00deb4c57a61");

    public const int S_OK = 0, E_NOTIMPL = unchecked((int)0x80004001), E_NOINTERFACE = unchecked((int)0x80004002), E_FAIL = unchecked((int)0x80004005),
        MF_E_SHUTDOWN = unchecked((int)0xC00D3E85), SET_NOT_FOUND = unchecked((int)0x80070492);
    public const uint MEStreamStarted = 202, MESourceStarted = 201, MENewStream = 205, MEUpdatedStream = 206, MESourceStopped = 207, MEStreamStopped = 208, MEMediaSample = 213;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public long h; }

static class Log
{
    public static void W(string s)
    {
        try
        {
            var p = K.Dir + @"\cam.log";
            if (File.Exists(p) && new FileInfo(p).Length > 200_000) return;
            File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\n");
        }
        catch { }
    }
}

// Semua metode PreserveSig: HRESULT dikembalikan apa adanya (callee managed memilih kodenya sendiri).
[ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid k, IntPtr v);
    [PreserveSig] int GetItemType(ref Guid k, out int t);
    [PreserveSig] int CompareItem(ref Guid k, IntPtr v, out int r);
    [PreserveSig] int Compare(IntPtr o, int type, out int r);
    [PreserveSig] int GetUINT32(ref Guid k, out uint v);
    [PreserveSig] int GetUINT64(ref Guid k, out ulong v);
    [PreserveSig] int GetDouble(ref Guid k, out double v);
    [PreserveSig] int GetGUID(ref Guid k, out Guid v);
    [PreserveSig] int GetStringLength(ref Guid k, out uint n);
    [PreserveSig] int GetString(ref Guid k, IntPtr buf, uint cch, IntPtr pcch);
    [PreserveSig] int GetAllocatedString(ref Guid k, out IntPtr s, out uint n);
    [PreserveSig] int GetBlobSize(ref Guid k, out uint n);
    [PreserveSig] int GetBlob(ref Guid k, IntPtr buf, uint size, IntPtr pSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid k, out IntPtr b, out uint n);
    [PreserveSig] int GetUnknown(ref Guid k, ref Guid riid, out IntPtr o);
    [PreserveSig] int SetItem(ref Guid k, IntPtr v);
    [PreserveSig] int DeleteItem(ref Guid k);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid k, uint v);
    [PreserveSig] int SetUINT64(ref Guid k, ulong v);
    [PreserveSig] int SetDouble(ref Guid k, double v);
    [PreserveSig] int SetGUID(ref Guid k, ref Guid v);
    [PreserveSig] int SetString(ref Guid k, [MarshalAs(UnmanagedType.LPWStr)] string v);
    [PreserveSig] int SetBlob(ref Guid k, IntPtr b, uint n);
    [PreserveSig] int SetUnknown(ref Guid k, IntPtr o);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint n);
    [PreserveSig] int GetItemByIndex(uint i, out Guid k, IntPtr v);
    [PreserveSig] int CopyAllItems(IntPtr dst);
}

static class Attr
{
    public static void U32(this IMFAttributes a, Guid k, uint v) => Marshal.ThrowExceptionForHR(a.SetUINT32(ref k, v));
    public static void U64(this IMFAttributes a, Guid k, ulong v) => Marshal.ThrowExceptionForHR(a.SetUINT64(ref k, v));
    public static void G(this IMFAttributes a, Guid k, Guid v) => Marshal.ThrowExceptionForHR(a.SetGUID(ref k, ref v));
}

[ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int _a00();
    [PreserveSig] int _a01();
    [PreserveSig] int _a02();
    [PreserveSig] int _a03();
    [PreserveSig] int _a04();
    [PreserveSig] int _a05();
    [PreserveSig] int _a06();
    [PreserveSig] int _a07();
    [PreserveSig] int _a08();
    [PreserveSig] int _a09();
    [PreserveSig] int _a10();
    [PreserveSig] int _a11();
    [PreserveSig] int _a12();
    [PreserveSig] int _a13();
    [PreserveSig] int _a14();
    [PreserveSig] int _a15();
    [PreserveSig] int _a16();
    [PreserveSig] int _a17();
    [PreserveSig] int _a18();
    [PreserveSig] int _a19();
    [PreserveSig] int _a20();
    [PreserveSig] int _a21();
    [PreserveSig] int _a22();
    [PreserveSig] int _a23();
    [PreserveSig] int _a24();
    [PreserveSig] int _a25();
    [PreserveSig] int _a26();
    [PreserveSig] int _a27();
    [PreserveSig] int _a28();
    [PreserveSig] int _a29();
    void GetMajorType(out Guid g);
    [PreserveSig] int IsCompressedFormat(out int b);
    [PreserveSig] int IsEqual(IntPtr o, out uint flags);
    [PreserveSig] int GetRepresentation(Guid g, out IntPtr r);
    [PreserveSig] int FreeRepresentation(Guid g, IntPtr r);
}

[ComImport, Guid("E93DCF6C-4B07-4E1E-8123-AA16ED6EADF5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaTypeHandler
{
    [PreserveSig] int IsMediaTypeSupported(IntPtr t, out IntPtr closest);
    void GetMediaTypeCount(out uint n);
    void GetMediaTypeByIndex(uint i, out IMFMediaType t);
    void SetCurrentMediaType(IMFMediaType t);
    void GetCurrentMediaType(out IMFMediaType t);
    void GetMajorType(out Guid g);
}

[ComImport, Guid("56C03D9C-9DBB-45F5-AB4B-D80F47C05938"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFStreamDescriptor : IMFAttributes
{
    [PreserveSig] int _a00();
    [PreserveSig] int _a01();
    [PreserveSig] int _a02();
    [PreserveSig] int _a03();
    [PreserveSig] int _a04();
    [PreserveSig] int _a05();
    [PreserveSig] int _a06();
    [PreserveSig] int _a07();
    [PreserveSig] int _a08();
    [PreserveSig] int _a09();
    [PreserveSig] int _a10();
    [PreserveSig] int _a11();
    [PreserveSig] int _a12();
    [PreserveSig] int _a13();
    [PreserveSig] int _a14();
    [PreserveSig] int _a15();
    [PreserveSig] int _a16();
    [PreserveSig] int _a17();
    [PreserveSig] int _a18();
    [PreserveSig] int _a19();
    [PreserveSig] int _a20();
    [PreserveSig] int _a21();
    [PreserveSig] int _a22();
    [PreserveSig] int _a23();
    [PreserveSig] int _a24();
    [PreserveSig] int _a25();
    [PreserveSig] int _a26();
    [PreserveSig] int _a27();
    [PreserveSig] int _a28();
    [PreserveSig] int _a29();
    void GetStreamIdentifier(out uint id);
    void GetMediaTypeHandler(out IMFMediaTypeHandler h);
}

[ComImport, Guid("03CB2711-24D7-4DB6-A17F-F3A7A479A536"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFPresentationDescriptor : IMFAttributes
{
    [PreserveSig] int _a00();
    [PreserveSig] int _a01();
    [PreserveSig] int _a02();
    [PreserveSig] int _a03();
    [PreserveSig] int _a04();
    [PreserveSig] int _a05();
    [PreserveSig] int _a06();
    [PreserveSig] int _a07();
    [PreserveSig] int _a08();
    [PreserveSig] int _a09();
    [PreserveSig] int _a10();
    [PreserveSig] int _a11();
    [PreserveSig] int _a12();
    [PreserveSig] int _a13();
    [PreserveSig] int _a14();
    [PreserveSig] int _a15();
    [PreserveSig] int _a16();
    [PreserveSig] int _a17();
    [PreserveSig] int _a18();
    [PreserveSig] int _a19();
    [PreserveSig] int _a20();
    [PreserveSig] int _a21();
    [PreserveSig] int _a22();
    [PreserveSig] int _a23();
    [PreserveSig] int _a24();
    [PreserveSig] int _a25();
    [PreserveSig] int _a26();
    [PreserveSig] int _a27();
    [PreserveSig] int _a28();
    [PreserveSig] int _a29();
    void GetStreamDescriptorCount(out uint n);
    void GetStreamDescriptorByIndex(uint i, out int selected, out IMFStreamDescriptor d);
    void SelectStream(uint i);
    void DeselectStream(uint i);
    void Clone(out IMFPresentationDescriptor c);
}

[ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaBuffer
{
    void Lock(out IntPtr p, out uint max, out uint cur);
    void Unlock();
    void GetCurrentLength(out uint n);
    void SetCurrentLength(uint n);
    void GetMaxLength(out uint n);
}

[ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSample : IMFAttributes
{
    [PreserveSig] int _a00();
    [PreserveSig] int _a01();
    [PreserveSig] int _a02();
    [PreserveSig] int _a03();
    [PreserveSig] int _a04();
    [PreserveSig] int _a05();
    [PreserveSig] int _a06();
    [PreserveSig] int _a07();
    [PreserveSig] int _a08();
    [PreserveSig] int _a09();
    [PreserveSig] int _a10();
    [PreserveSig] int _a11();
    [PreserveSig] int _a12();
    [PreserveSig] int _a13();
    [PreserveSig] int _a14();
    [PreserveSig] int _a15();
    [PreserveSig] int _a16();
    [PreserveSig] int _a17();
    [PreserveSig] int _a18();
    [PreserveSig] int _a19();
    [PreserveSig] int _a20();
    [PreserveSig] int _a21();
    [PreserveSig] int _a22();
    [PreserveSig] int _a23();
    [PreserveSig] int _a24();
    [PreserveSig] int _a25();
    [PreserveSig] int _a26();
    [PreserveSig] int _a27();
    [PreserveSig] int _a28();
    [PreserveSig] int _a29();
    void GetSampleFlags(out uint f);
    void SetSampleFlags(uint f);
    void GetSampleTime(out long t);
    void SetSampleTime(long t);
    void GetSampleDuration(out long t);
    void SetSampleDuration(long t);
    void GetBufferCount(out uint n);
    void GetBufferByIndex(uint i, out IMFMediaBuffer b);
    void ConvertToContiguousBuffer(out IMFMediaBuffer b);
    void AddBuffer(IMFMediaBuffer b);
    void RemoveBufferByIndex(uint i);
    void RemoveAllBuffers();
    void GetTotalLength(out uint n);
    void CopyToBuffer(IMFMediaBuffer b);
}

[ComImport, Guid("36F846FC-2256-48B6-B58E-E2B638316581"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaEventQueue
{
    [PreserveSig] int GetEvent(uint flags, out IntPtr ev);
    [PreserveSig] int BeginGetEvent(IntPtr cb, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr res, out IntPtr ev);
    [PreserveSig] int QueueEvent(IntPtr ev);
    [PreserveSig] int QueueEventParamVar(uint met, ref Guid ext, int hr, IntPtr pv);
    [PreserveSig] int QueueEventParamUnk(uint met, ref Guid ext, int hr, IntPtr unk);
    [PreserveSig] int Shutdown();
}

// ---- antarmuka yang diimplementasikan objek managed (dipanggil Frame Server) ----
[ComImport, Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFActivate
{
    [PreserveSig] int GetItem(ref Guid k, IntPtr v);
    [PreserveSig] int GetItemType(ref Guid k, out int t);
    [PreserveSig] int CompareItem(ref Guid k, IntPtr v, out int r);
    [PreserveSig] int Compare(IntPtr o, int type, out int r);
    [PreserveSig] int GetUINT32(ref Guid k, out uint v);
    [PreserveSig] int GetUINT64(ref Guid k, out ulong v);
    [PreserveSig] int GetDouble(ref Guid k, out double v);
    [PreserveSig] int GetGUID(ref Guid k, out Guid v);
    [PreserveSig] int GetStringLength(ref Guid k, out uint n);
    [PreserveSig] int GetString(ref Guid k, IntPtr buf, uint cch, IntPtr pcch);
    [PreserveSig] int GetAllocatedString(ref Guid k, out IntPtr s, out uint n);
    [PreserveSig] int GetBlobSize(ref Guid k, out uint n);
    [PreserveSig] int GetBlob(ref Guid k, IntPtr buf, uint size, IntPtr pSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid k, out IntPtr b, out uint n);
    [PreserveSig] int GetUnknown(ref Guid k, ref Guid riid, out IntPtr o);
    [PreserveSig] int SetItem(ref Guid k, IntPtr v);
    [PreserveSig] int DeleteItem(ref Guid k);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid k, uint v);
    [PreserveSig] int SetUINT64(ref Guid k, ulong v);
    [PreserveSig] int SetDouble(ref Guid k, double v);
    [PreserveSig] int SetGUID(ref Guid k, ref Guid v);
    [PreserveSig] int SetString(ref Guid k, [MarshalAs(UnmanagedType.LPWStr)] string v);
    [PreserveSig] int SetBlob(ref Guid k, IntPtr b, uint n);
    [PreserveSig] int SetUnknown(ref Guid k, IntPtr o);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint n);
    [PreserveSig] int GetItemByIndex(uint i, out Guid k, IntPtr v);
    [PreserveSig] int CopyAllItems(IntPtr dst);
    [PreserveSig] int ActivateObject(ref Guid riid, out IntPtr obj);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaEventGenerator
{
    [PreserveSig] int GetEvent(uint flags, out IntPtr ev);
    [PreserveSig] int BeginGetEvent(IntPtr cb, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr res, out IntPtr ev);
    [PreserveSig] int QueueEvent(uint met, ref Guid ext, int hr, IntPtr pv);
}

[ComImport, Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaSource
{
    [PreserveSig] int GetEvent(uint flags, out IntPtr ev);
    [PreserveSig] int BeginGetEvent(IntPtr cb, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr res, out IntPtr ev);
    [PreserveSig] int QueueEvent(uint met, ref Guid ext, int hr, IntPtr pv);
    [PreserveSig] int GetCharacteristics(out uint c);
    [PreserveSig] int CreatePresentationDescriptor(out IntPtr pd);
    [PreserveSig] int Start(IntPtr pd, IntPtr timeFmt, IntPtr startPos);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
}

[ComImport, Guid("3C9B2EB9-86D5-4514-A394-F56664F9F0D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaSourceEx
{
    [PreserveSig] int GetEvent(uint flags, out IntPtr ev);
    [PreserveSig] int BeginGetEvent(IntPtr cb, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr res, out IntPtr ev);
    [PreserveSig] int QueueEvent(uint met, ref Guid ext, int hr, IntPtr pv);
    [PreserveSig] int GetCharacteristics(out uint c);
    [PreserveSig] int CreatePresentationDescriptor(out IntPtr pd);
    [PreserveSig] int Start(IntPtr pd, IntPtr timeFmt, IntPtr startPos);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
    [PreserveSig] int GetSourceAttributes(out IntPtr a);
    [PreserveSig] int GetStreamAttributes(uint id, out IntPtr a);
    [PreserveSig] int SetD3DManager(IntPtr mgr);
}

[ComImport, Guid("D182108F-4EC6-443F-AA42-A71106EC825F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaStream
{
    [PreserveSig] int GetEvent(uint flags, out IntPtr ev);
    [PreserveSig] int BeginGetEvent(IntPtr cb, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr res, out IntPtr ev);
    [PreserveSig] int QueueEvent(uint met, ref Guid ext, int hr, IntPtr pv);
    [PreserveSig] int GetMediaSource(out IntPtr src);
    [PreserveSig] int GetStreamDescriptor(out IntPtr sd);
    [PreserveSig] int RequestSample(IntPtr token);
}

[ComImport, Guid("C5BC37D6-75C7-46A1-A132-81B5F723C20F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaStream2
{
    [PreserveSig] int GetEvent(uint flags, out IntPtr ev);
    [PreserveSig] int BeginGetEvent(IntPtr cb, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr res, out IntPtr ev);
    [PreserveSig] int QueueEvent(uint met, ref Guid ext, int hr, IntPtr pv);
    [PreserveSig] int GetMediaSource(out IntPtr src);
    [PreserveSig] int GetStreamDescriptor(out IntPtr sd);
    [PreserveSig] int RequestSample(IntPtr token);
    [PreserveSig] int SetStreamState(int state);
    [PreserveSig] int GetStreamState(out int state);
}

[ComImport, Guid("FA993888-4383-415A-A930-DD472A8CF6F7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFGetService
{
    [PreserveSig] int GetService(ref Guid svc, ref Guid riid, out IntPtr obj);
}

[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IKsControl
{
    [PreserveSig] int KsProperty(IntPtr prop, uint propLen, IntPtr data, uint dataLen, out uint ret);
    [PreserveSig] int KsMethod(IntPtr m, uint mLen, IntPtr data, uint dataLen, out uint ret);
    [PreserveSig] int KsEvent(IntPtr e, uint eLen, IntPtr data, uint dataLen, out uint ret);
}

static class MF
{
    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes a, uint size);
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType t);
    [DllImport("mfplat.dll")] public static extern int MFCreateStreamDescriptor(uint id, uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IMFMediaType[] types, out IMFStreamDescriptor sd);
    [DllImport("mfplat.dll")] public static extern int MFCreatePresentationDescriptor(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IMFStreamDescriptor[] sds, out IMFPresentationDescriptor pd);
    [DllImport("mfplat.dll")] public static extern int MFCreateEventQueue(out IMFMediaEventQueue q);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample s);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint size, out IMFMediaBuffer b);
    [DllImport("mfplat.dll")] public static extern long MFGetSystemTime();

    static bool _up;
    public static void Up() { if (_up) return; _up = true; MFStartup(0x00020070, 0); }
}

/// <summary>IMFAttributes yang meneruskan ke penyimpanan atribut MF; dasar untuk activator, source dan stream.</summary>
public abstract class AttrBase : IMFAttributes
{
    protected readonly IMFAttributes A;
    protected AttrBase() { MF.Up(); Marshal.ThrowExceptionForHR(MF.MFCreateAttributes(out A, 8)); }
    public int GetItem(ref Guid k, IntPtr v) => A.GetItem(ref k, v);
    public int GetItemType(ref Guid k, out int t) => A.GetItemType(ref k, out t);
    public int CompareItem(ref Guid k, IntPtr v, out int r) => A.CompareItem(ref k, v, out r);
    public int Compare(IntPtr o, int type, out int r) => A.Compare(o, type, out r);
    public int GetUINT32(ref Guid k, out uint v) => A.GetUINT32(ref k, out v);
    public int GetUINT64(ref Guid k, out ulong v) => A.GetUINT64(ref k, out v);
    public int GetDouble(ref Guid k, out double v) => A.GetDouble(ref k, out v);
    public int GetGUID(ref Guid k, out Guid v) => A.GetGUID(ref k, out v);
    public int GetStringLength(ref Guid k, out uint n) => A.GetStringLength(ref k, out n);
    public int GetString(ref Guid k, IntPtr buf, uint cch, IntPtr pcch) => A.GetString(ref k, buf, cch, pcch);
    public int GetAllocatedString(ref Guid k, out IntPtr s, out uint n) => A.GetAllocatedString(ref k, out s, out n);
    public int GetBlobSize(ref Guid k, out uint n) => A.GetBlobSize(ref k, out n);
    public int GetBlob(ref Guid k, IntPtr buf, uint size, IntPtr pSize) => A.GetBlob(ref k, buf, size, pSize);
    public int GetAllocatedBlob(ref Guid k, out IntPtr b, out uint n) => A.GetAllocatedBlob(ref k, out b, out n);
    public int GetUnknown(ref Guid k, ref Guid riid, out IntPtr o) => A.GetUnknown(ref k, ref riid, out o);
    public int SetItem(ref Guid k, IntPtr v) => A.SetItem(ref k, v);
    public int DeleteItem(ref Guid k) => A.DeleteItem(ref k);
    public int DeleteAllItems() => A.DeleteAllItems();
    public int SetUINT32(ref Guid k, uint v) => A.SetUINT32(ref k, v);
    public int SetUINT64(ref Guid k, ulong v) => A.SetUINT64(ref k, v);
    public int SetDouble(ref Guid k, double v) => A.SetDouble(ref k, v);
    public int SetGUID(ref Guid k, ref Guid v) => A.SetGUID(ref k, ref v);
    public int SetString(ref Guid k, string v) => A.SetString(ref k, v);
    public int SetBlob(ref Guid k, IntPtr b, uint n) => A.SetBlob(ref k, b, n);
    public int SetUnknown(ref Guid k, IntPtr o) => A.SetUnknown(ref k, o);
    public int LockStore() => A.LockStore();
    public int UnlockStore() => A.UnlockStore();
    public int GetCount(out uint n) => A.GetCount(out n);
    public int GetItemByIndex(uint i, out Guid k, IntPtr v) => A.GetItemByIndex(i, out k, v);
    public int CopyAllItems(IntPtr dst) => A.CopyAllItems(dst);
}
