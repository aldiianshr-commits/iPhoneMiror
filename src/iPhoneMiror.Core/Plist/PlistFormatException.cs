namespace iPhoneMiror.Core.Plist;

public sealed class PlistFormatException : Exception
{
    public PlistFormatException(string message) : base(message) { }
    public PlistFormatException(string message, Exception inner) : base(message, inner) { }
}
