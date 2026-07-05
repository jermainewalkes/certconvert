namespace CertConvert.Core;

/// <summary>Base for all errors the tool surfaces to users.</summary>
public class CertConvertException : Exception
{
    public CertConvertException(string message) : base(message) { }
    public CertConvertException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The input is encrypted and no password was supplied.</summary>
public sealed class PasswordRequiredException : CertConvertException
{
    public PasswordRequiredException(string what)
        : base($"{what} is password-protected. Supply a password to open it.") { }

    /// <summary>Re-raises a password error with the offending file's name attached.</summary>
    public PasswordRequiredException(string fileName, PasswordRequiredException inner)
        : base($"{fileName}: {inner.Message}", inner) { }
}

/// <summary>A password was supplied but does not decrypt the input.</summary>
public sealed class InvalidPasswordException : CertConvertException
{
    public InvalidPasswordException(string what)
        : base($"The password does not match {what}.") { }

    /// <summary>Re-raises a password error with the offending file's name attached.</summary>
    public InvalidPasswordException(string fileName, InvalidPasswordException inner)
        : base($"{fileName}: {inner.Message}", inner) { }
}

/// <summary>The input bytes could not be recognised as any supported format.</summary>
public sealed class UnrecognisedContentException : CertConvertException
{
    public UnrecognisedContentException(string message) : base(message) { }
}
