using System;

namespace Potok.Backend.Core.Models.Gateway;

public class TraktUnauthorizedException : UnauthorizedAccessException
{
    public TraktUnauthorizedException() : base("Trakt authorization failed")
    {
    }

    public TraktUnauthorizedException(string message) : base(message)
    {
    }
}
