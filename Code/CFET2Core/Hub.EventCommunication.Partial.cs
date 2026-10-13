namespace Jtext103.CFET2.Core
{
    public partial class Hub
    {
        public Config.EventCommunicationConfiguration ActiveEventCommunicationConfiguration => myMaster.MyCommunicationManager.ActiveEventCommunicationConfiguration;
    }
}
