using System.Runtime.InteropServices;

namespace BetterTranslator.Updates.Payload;

public enum SignatureState
{
    Unsigned,
    Valid,
    Invalid,
}

public sealed record SignatureVerdict(SignatureState State, string Detail);

public static class Authenticode
{
    private const uint UiNone = 2;
    private const uint RevocationNone = 0;
    private const uint ChoiceFile = 1;
    private const uint ActionVerify = 1;
    private const uint ActionClose = 2;
    private const uint SaferFlag = 0x100;

    private const int TrustNoSignature = unchecked((int)0x800B0100);
    private const int ProviderUnknown = unchecked((int)0x800B0001);
    private const int SubjectFormUnknown = unchecked((int)0x800B0003);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static SignatureVerdict Verify(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SignatureVerdict(SignatureState.Unsigned, "Authenticode is a Windows check.");
        }

        var fileInfo = new WintrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = Marshal.StringToHGlobalUni(file),
            File = IntPtr.Zero,
            KnownSubject = IntPtr.Zero,
        };

        var fileInfoBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        var dataBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>());

        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoBuffer, fDeleteOld: false);

            var data = new WintrustData
            {
                Size = (uint)Marshal.SizeOf<WintrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                UiChoice = UiNone,
                RevocationChecks = RevocationNone,
                UnionChoice = ChoiceFile,
                Union = fileInfoBuffer,
                StateAction = ActionVerify,
                StateData = IntPtr.Zero,
                UrlReference = IntPtr.Zero,
                ProviderFlags = SaferFlag,
                UiContext = 0,
                SignatureSettings = IntPtr.Zero,
            };

            Marshal.StructureToPtr(data, dataBuffer, fDeleteOld: false);

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, dataBuffer);

            data = Marshal.PtrToStructure<WintrustData>(dataBuffer);
            data.StateAction = ActionClose;
            Marshal.StructureToPtr(data, dataBuffer, fDeleteOld: false);
            WinVerifyTrust(IntPtr.Zero, ref action, dataBuffer);

            return result switch
            {
                0 => new SignatureVerdict(SignatureState.Valid, "The Authenticode signature is valid."),
                TrustNoSignature or ProviderUnknown or SubjectFormUnknown =>
                    new SignatureVerdict(SignatureState.Unsigned, "The file carries no Authenticode signature."),
                _ => new SignatureVerdict(
                    SignatureState.Invalid,
                    $"The Authenticode signature did not verify (0x{result:X8})."),
            };
        }
        catch (DllNotFoundException)
        {
            return new SignatureVerdict(SignatureState.Unsigned, "This machine has no signature verification provider.");
        }
        catch (EntryPointNotFoundException)
        {
            return new SignatureVerdict(SignatureState.Unsigned, "This machine has no signature verification provider.");
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo.FilePath);
            Marshal.FreeHGlobal(fileInfoBuffer);
            Marshal.FreeHGlobal(dataBuffer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr Union;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
