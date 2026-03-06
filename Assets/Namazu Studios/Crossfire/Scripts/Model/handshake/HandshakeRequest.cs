using Newtonsoft.Json;

namespace Elements.Crossfire.Model
{
    /**
     * Represents a request for the mode of the WebSocket. The first message sent must be a request type in order to put the
     * socket into the correct mode for processing.
     */
    public interface HandshakeRequest : ProtocolMessage
    {

        /**
         * THe version 1.0 of the Crossfire protocol.
         */
        const string VERSION_1_0 = "V_1_0";

        /**
         * THe version 1.1 of the Crossfire protocol.
         */
        const string VERSION_1_1 = "V_1_1";

        /**
         * Gets the version of the protocol requested.
         *
         * @return the version of the protocol
         */
        string GetVersion();

        /**
         * The profile id of the user making the request
         * @return the profile id
         */
        string GetProfileId();

        /**
         * The session key.
         *
         * @return the session key
         */
        string GetSessionKey();

    }

    public static class HandshakeRequestExtensions
    {
        public static string ToJsonString<T>(this HandshakeRequest request)
        {
            var serializerSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                Converters = new System.Collections.Generic.List<JsonConverter> { new Newtonsoft.Json.Converters.StringEnumConverter() }
            };

            return JsonConvert.SerializeObject(request, serializerSettings);
        }
    }
}