using System;
using Newtonsoft.Json;

namespace Elements.Crossfire.Model
{
    public class CreateHandshakeRequest : HandshakeRequest {

        [JsonProperty]
        private string version = HandshakeRequest.VERSION_1_1;

        [JsonProperty]
        private string profileId;

        [JsonProperty]
        private string sessionKey;

        [JsonProperty]
        private string configuration;

        [JsonProperty]
        private MessageType type = MessageType.CREATE;

        public string GetVersion() {
            return version;
        }

        public void SetVersion(string version) {
            this.version = version;
        }

        public MessageType GetMessageType() {
            return type;
        }

        public string GetProfileId() {
            return profileId;
        }

        public void SetProfileId(string profileId) {
            this.profileId = profileId;
        }

        public string GetSessionKey() {
            return sessionKey;
        }

        public void SetSessionKey(string sessionKey) {
            this.sessionKey = sessionKey;
        }

        public string GetConfiguration() {
            return configuration;
        }

        public void SetConfiguration(string configuration) {
            this.configuration = configuration;
        }

    }
}