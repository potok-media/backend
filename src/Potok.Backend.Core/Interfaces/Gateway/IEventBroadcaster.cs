using System;
using System.Collections.Generic;
using System.Threading;

namespace Potok.Backend.Core.Interfaces.Gateway;

public interface IEventBroadcaster
{
    void Publish<T>(string eventName, T data, Guid? userId = null);
}

