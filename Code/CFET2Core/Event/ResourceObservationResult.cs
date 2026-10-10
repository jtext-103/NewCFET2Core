namespace Jtext103.CFET2.Core.Event
{
    public enum ResourceObservationStatus { Started, Shared, Skipped, Failed }
    public enum ResourceObservationReason { None, DeniedByAutoDenyList, ListMatchFailed }

    /// <summary>Acquisition-time result, not a live health report. The caller owns Handle when non-null.</summary>
    public sealed class ResourceObservationResult
    {
        internal ResourceObservationResult(string path, ResourceObservationStatus status,
            string reason = null, ObservationHandle handle = null,
            ResourceObservationReason reasonCode = ResourceObservationReason.None)
        {
            ResourcePath = path;
            Status = status;
            Reason = reason;
            Handle = handle;
            ReasonCode = reasonCode;
        }

        public string ResourcePath { get; }
        public ResourceObservationStatus Status { get; }
        public string Reason { get; }
        public ResourceObservationReason ReasonCode { get; }
        public ObservationHandle Handle { get; }
    }
}
