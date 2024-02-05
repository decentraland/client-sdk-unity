using System;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;

namespace LiveKit.Internal
{
    //TODO move to struct, IDisposable
    public class FfiHandle : IDisposable
    {
        private IntPtr handle;
        private bool isClosed;

        public bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        
        public bool IsClosed => isClosed;

        internal void Construct(IntPtr intPtr)
        {
            handle = intPtr;
            isClosed = false;
        }
        
        internal void Clear()
        {
            handle = IntPtr.Zero;
            isClosed = false;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IntPtr DangerousGetHandle()
        {
            return handle;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetHandleAsInvalid()
        {
            isClosed = true;
            Dispose();
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        public void Dispose()
        {
            if (IsInvalid)
            {
                return;
            }
            NativeMethods.FfiDropHandle(handle);
            handle = IntPtr.Zero;
        }
    }
}