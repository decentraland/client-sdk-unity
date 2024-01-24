using LiveKit.Proto;

namespace LiveKit.Internal.FFIClients
{
    public interface IFFIClient
    {
        FfiResponse SendRequest(FfiRequest request);
    }
}