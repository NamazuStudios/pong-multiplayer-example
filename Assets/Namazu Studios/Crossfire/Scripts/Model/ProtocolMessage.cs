
namespace Elements.Crossfire.Model
{

    public interface ProtocolMessage
    {

        /**
         * Returns the type of the message.
         *
         * @return the type
         */
        MessageType GetMessageType();

    }

    public enum MessageType
    {

        /**
         * Indicates the request is to find a match. The server will select
         */
        FIND,

        /**
         * Indicates the request is to join a match. The server will select the specific match.
         */
        JOIN,

        /**
         * Indicates that the client has successfully connected to a match.
         */
        MATCHED,

        /**
         * Indicates the request is to join a match. The server will select the specific match.
         */
        CREATE,
        
        /**
         * Indicates the request is to join a match. The server will select the specific match.
         */
        CREATED,
        
        /**
         * Indicates the request is to join a match. The server will select the specific match.
         */
        JOIN_CODE,
        
        /**
         * Represents a signal indicating that a profile reconnected to the session.
         */
        CONNECT,

        /**
         * Represents a signal indicating that a profile disconnected from the session.
         */
        DISCONNECT,

        /**
         * Represents a signal that carries an SDP offer.
         */
        SDP_OFFER,

        /**
         * Represents a signal that carries an SDP answer.
         */
        SDP_ANSWER,

        /**
         * Represents a signal that carries a binary payload to be broadcasted to all profiles in the match.
         */
        BINARY_BROADCAST,

        /**
         * Represents a signal that carries a binary payload to be relayed to a specific profile in the match.
         */
        BINARY_RELAY,

        /**
         * Represents a signal that carries a candidate for the WebRTC connection.
         */
        CANDIDATE,

        /**
         * Specifies the designated HOST profile
         */
        HOST,

        /**
         * Represents a signal that carries a message to be sent to the host.
         */
        ERROR,

        /**
        * Requests that the client leave the match.
        */
        LEAVE,

        /**
         * Requests that the client open the match for new participants.
         */
        OPEN,

        /**
         * Requests that the client close the match for new participants.
         */
        CLOSE,

        /**
         * Requests that the client leave the match and close the match for new participants.
         */
        END,

        /**
          * Specifies when a participant has joined the match.
          */
        SIGNAL_JOIN,

        /**
         * Specifies when a participant has left the match.
         */
        SIGNAL_LEAVE,
    }

    /**
     * The category of the protocol message. The life of the websocket connection is divided into categories for each
     * phase of the matchmaking system.
     */
    public enum Category
    {

        /**
         * Used in the handshake process, before a match starts.
         */
        HANDSHAKE,

        /**
         * Used in the signaling process, after a match starts signals in this category will be sent to all profiles in
         * the match via the signaling server.
         */
        SIGNALING,

        /**
         * Used for direct signaling between profiles in a match. After a match starts, profiles can send messages
         * directly to the recipient profile via the signaling server.
         **/
        SIGNALING_DIRECT,

        /**
         * Used for error messages.
         */
        ERROR

    }
}