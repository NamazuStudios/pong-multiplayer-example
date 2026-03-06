using Newtonsoft.Json;

namespace Elements.Crossfire.Model
{
    public class CreatedHandshakeResponse : HandshakeResponse
    {
        [JsonProperty]
        private string matchId;

        [JsonProperty]
        private string joinCode;

        [JsonProperty]
        private string profileId;
        
        public string GetMatchId() {
            return matchId;
        }

        public void SetMatchId(string matchId) {
            this.matchId = matchId;
        }
        
        public string GetProfileId() {
            return profileId;
        }

        public void SetProfileId(string profileId) {
            this.profileId = profileId;
        }

        
        public MessageType GetMessageType() {
            return MessageType.CREATED;
        }

        public string GetJoinCode() {
            return joinCode;
        }

        public void SetJoinCode(string joinCode) {
            this.joinCode = joinCode;
        }   
    }
}