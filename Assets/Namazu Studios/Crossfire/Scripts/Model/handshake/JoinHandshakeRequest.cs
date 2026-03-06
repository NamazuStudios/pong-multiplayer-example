using Newtonsoft.Json;

namespace Elements.Crossfire.Model
{
    public class JoinHandshakeRequest : HandshakeRequest
    {

        [JsonProperty]
        private string version = HandshakeRequest.VERSION_1_0;

        [JsonProperty]
        private string profileId;

        [JsonProperty]
        private string sessionKey;

        [JsonProperty]
        private string matchId;

        [JsonProperty]
        private MessageType type = MessageType.JOIN;

        public MessageType GetMessageType()
        {
            return type;
        }

        public string GetVersion()
        {
            return version;
        }

        public void SetVersion(string version)
        {
            this.version = version;
        }

        public string GetProfileId()
        {
            return profileId;
        }

        public void SetProfileId(string profileId)
        {
            this.profileId = profileId;
        }

        public string GetMatchId()
        {
            return matchId;
        }

        public void SetMatchId(string matchId)
        {
            this.matchId = matchId;
        }

        public string GetSessionKey()
        {
            return sessionKey;
        }

        public void SetSessionKey(string sessionKey)
        {
            this.sessionKey = sessionKey;
        }

    }
}