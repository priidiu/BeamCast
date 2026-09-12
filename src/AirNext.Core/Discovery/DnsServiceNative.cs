using System.Runtime.InteropServices;

namespace AirNext.Core.Discovery;

/// <summary>
/// Natywny DNS-SD na Windows (RHI-160) — P/Invoke do dnsapi.dll: DnsServiceBrowse/Resolve.
/// Windows 10 1607+ ma wbudowany mDNS resolver — to jest EXACTLY to czego uses
/// TuneBlade (Mono.Zeroconf → natywny DNS-SD). Solves problem "nasz own socket
/// koliduje z natywnym resolverem o port 5353 i czasem nie odbiera multicast".
///
/// UWAGA (fix 0x57): DnsServiceBrowse/Resolve take JEDEN argument — pointer do
/// struktury request (Version=1, InterfaceIndex=0, QueryName, callback, context).
/// QueryName musi live po powrocie (API asynchroniczne) — alokujemy manually.
/// </summary>
internal static class DnsServiceNative
{
    internal const uint DnsSuccess = 0;      // ERROR_SUCCESS
    internal const uint DnsInfoNoRecords = 9501;
    internal const uint DnsRequestPending = 9503;
    internal const uint DnsQueryRequestVersion1 = 1;

    // DNS_SERVICE_BROWSE_REQUEST (winnt/dnssd.h)
    [StructLayout(LayoutKind.Sequential)]
    internal struct DnsServiceBrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
 public IntPtr QueryName; // LPWSTR — manually alokowany
        public IntPtr BrowseCallback;  // function pointer
        public IntPtr BrowseContext;
    }

    // DNS_SERVICE_RESOLVE_REQUEST
    [StructLayout(LayoutKind.Sequential)]
    internal struct DnsServiceResolveRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr ResolveCallback;
        public IntPtr ResolveContext;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint DnsServiceBrowse(ref DnsServiceBrowseRequest pRequest);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint DnsServiceResolve(ref DnsServiceResolveRequest pRequest);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint DnsServiceBrowseCancel(ref DnsServiceBrowseRequest pRequest);

    // Callback browse: Status, pQueryContext, pInstance (DNS_SERVICE_INSTANCE*)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void DnsServiceBrowseCallback(uint status, IntPtr pQueryContext, IntPtr pInstance);

    // Callback resolve: Status, pQueryContext, pInstance
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void DnsServiceResolveCallback(uint status, IntPtr pQueryContext, IntPtr pInstance);

 /// <summary>Alokuje LPWSTR (musi live po powrocie — API asynchroniczne).</summary>
    internal static IntPtr AllocString(string s) => Marshal.StringToHGlobalUni(s);

 /// <summary>Zwraca function pointer dla delegata (keep-alive po stronie caller).</summary>
    internal static IntPtr FnPtr(Delegate d) => Marshal.GetFunctionPointerForDelegate(d);

 /// <summary>Czyta DNS_SERVICE_INSTANCE z memory (bezpiecznie przez Marshal).</summary>
    internal static (string InstanceName, string HostName, int Port, int InterfaceIndex, string Text)? ReadInstance(IntPtr ptr)
    {
        if (ptr == 0)
            return null;

        try
        {
            IntPtr pszInstance = Marshal.ReadIntPtr(ptr, 0);
            IntPtr pszHost = Marshal.ReadIntPtr(ptr, nint.Size);
            int port = Marshal.ReadInt32(ptr, nint.Size * 2);
            int interfaceIndex = Marshal.ReadInt32(ptr, nint.Size * 2 + 8);
            IntPtr pszText = Marshal.ReadIntPtr(ptr, nint.Size * 3 + 8);

            string instanceName = pszInstance != 0 ? Marshal.PtrToStringUni(pszInstance) ?? "" : "";
            string hostName = pszHost != 0 ? Marshal.PtrToStringUni(pszHost) ?? "" : "";
            string text = pszText != 0 ? Marshal.PtrToStringUni(pszText) ?? "" : "";

            return (instanceName, hostName, port, interfaceIndex, text);
        }
        catch
        {
            return null;
        }
    }
}
