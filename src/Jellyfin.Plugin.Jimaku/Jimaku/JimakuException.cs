using System;
using System.Net;

namespace Jellyfin.Plugin.Jimaku.Jimaku;

/// <summary>
/// Raised when the Jimaku API returns an error, carrying enough detail for the UI to say something
/// actionable rather than "request failed".
/// </summary>
public class JimakuApiException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="JimakuApiException"/> class.</summary>
    public JimakuApiException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JimakuApiException"/> class.</summary>
    /// <param name="message">The message.</param>
    public JimakuApiException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JimakuApiException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public JimakuApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JimakuApiException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="errorCode">The Jimaku error code, if one was returned.</param>
    public JimakuApiException(string message, HttpStatusCode statusCode, int errorCode)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets the Jimaku error code.</summary>
    public int ErrorCode { get; }

    /// <summary>Gets a value indicating whether the API key was rejected.</summary>
    public bool IsAuthenticationFailure => StatusCode == HttpStatusCode.Unauthorized || ErrorCode == 7;

    /// <summary>Gets a value indicating whether the request was rate limited.</summary>
    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests || ErrorCode == 8;
}
