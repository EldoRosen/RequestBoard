using System;

namespace RequestBoard.Backend
{
    public class BackendUnavailableException : Exception
    {
        public BackendUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }
}
