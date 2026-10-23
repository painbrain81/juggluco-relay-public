namespace JugglucoRelay.Protocol;

/// <summary>Mirror protocol constants. References in docs/protocol/spec-1-connection.md.</summary>
public static class Wire
{
    public const int LabelLength = 16;
    public const int SendMagicLength = 14;
    public const int ReceiveMagicLength = 15;
    public const int NonceHalfLength = 8;

    /// <summary>First 10 bytes of the connecting side's magic (Juggluco net/sendmagicinit.h:22).</summary>
    public static ReadOnlySpan<byte> SendMagicPrefix => [0xDA, 0xAD, 0xBE, 0xED, 0xDE, 0xED, 0xBE, 0xEF, 0xD1, 0xEF];

    /// <summary>Reply of the accepting side, without the last byte (Juggluco net/receivemagic.h:22).</summary>
    public static ReadOnlySpan<byte> ReceiveMagicPrefix => [0x67, 0xF9, 0x2D, 0x42, 0x34, 0x80, 0x28, 0xDE, 0xBA, 0x81, 0x3F, 0x00, 0x00, 0x00];

    /// <summary>Reply to a <c>sack</c>: always unencrypted, even on encrypted connections.</summary>
    public static ReadOnlySpan<byte> AckReply => [0x11, 0x00, 0x0E, 0x00];

    /// <summary><c>sack</c> command: the sender asks for an ack of everything before it.</summary>
    public static ReadOnlySpan<byte> AckRequest => [0x0C, 0x00, 0x01, 0x00];

    /// <summary>Juggluco's limit for an encrypted frame (MAXDATA, Juggluco net/getcommand.cpp:480).</summary>
    public const int MaxFrameData = 256 * 1024 * 1024;

    public static int Align4(int n) => (n + 3) & ~3;
}

/// <summary>Command codes (Juggluco net/comtypes.hpp:26-48).</summary>
public enum Command : ushort
{
    Open = 1,
    MakeLength = 2,
    Write = 3,
    Close = 4,
    Backup = 5,
    BackupStop = 7,
    Render = 8,
    Nums = 10,
    NumNr = 11,
    Ack = 12,
    NumInit = 13,
    AskLastNum = 14,
    AskFile = 15,
    UpToDate = 16,
    FileOnce = 17,
    WakeupStream = 18,
    Glucose = 19,
    ResetDevices = 20,
    StartSendCalibrate = 21,
    BlueWatch = 22,
}
