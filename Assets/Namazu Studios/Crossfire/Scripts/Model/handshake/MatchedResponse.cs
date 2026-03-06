using Newtonsoft.Json;

namespace Elements.Crossfire.Model
{
    /**
     * Indicates that the client has successfully connected to a matched.
     */
    public class MatchedResponse : HandshakeResponse
    {

        [JsonProperty]
        private string matchId;

        [JsonProperty] 
        private string profileId;
        
        public MessageType GetMessageType()
        {
            return MessageType.MATCHED;
        }

        /**
         * Returns the matched ID of the connected matched.
         *
         * @return the matched id
         */
        public string GetMatchId()
        {
            return matchId;
        }

        public void SetMatchId(string matchId)
        {
            this.matchId = matchId;
        }

        public string GetProfileId()
        {
            return profileId;
        }

        public void SetProfileId(string profileId)
        {
            this.profileId = profileId;
        }

    }
}