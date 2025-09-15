using System;
using RichTypes;

namespace LiveKit.Types
{
    public class Owned<T> where T: class
    {
        private T? resource;
        private bool disposed;

        public T Resource => disposed ? throw new ObjectDisposedException(typeof(T).Name) : resource!;

        public bool Disposed => disposed;

        public Owned(T resource)
        {
            this.resource = resource;
            disposed = false;
        }

        /// <summary>
        /// Caller is responsible to manually dispose the inner resource
        /// </summary>
        public void Dispose(out T? inner)
        {
            disposed = true;
            inner = resource;
            resource = null;
        }

        public Weak<T> Downgrade() =>
            new (this);
    }

    public readonly struct Weak<T> where T: class
    {
        public static Weak<T> Null;

        static Weak()
        {
            Owned<T> empty = new Owned<T>(null!);
            empty.Dispose(out _);
            Null = new Weak<T>(empty);
        }

        private readonly Owned<T> ownedResource;

        public Option<T> Resource => ownedResource.Disposed ? Option<T>.None : Option<T>.Some(ownedResource.Resource);

        internal Weak(Owned<T> ownedResource)
        {
            this.ownedResource = ownedResource;
        }
    }
}