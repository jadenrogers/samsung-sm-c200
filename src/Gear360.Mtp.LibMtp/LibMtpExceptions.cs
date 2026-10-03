namespace Gear360.Mtp.LibMtp;

/// <summary>The native libmtp library could not be found or loaded. The message says how to install it.</summary>
public sealed class LibMtpNotFoundException : DllNotFoundException
{
    /// <summary>Creates the exception with an install hint as its message.</summary>
    public LibMtpNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a default message.</summary>
    public LibMtpNotFoundException()
        : base("libmtp was not found.")
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public LibMtpNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A libmtp call failed. The message includes libmtp's own error text when it gave any.</summary>
public sealed class LibMtpException : IOException
{
    /// <summary>Creates the exception.</summary>
    public LibMtpException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a default message.</summary>
    public LibMtpException()
        : base("A libmtp call failed.")
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public LibMtpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
