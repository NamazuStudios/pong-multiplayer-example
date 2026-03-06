namespace Elements.Crossfire.Model
{
    /**
     * Interface representing a response to a handshake request in the Elements Crossfire protocol.
     */
    public interface HandshakeResponse : ProtocolMessage
    {

        /**
         * Gets the match ID of the connected match.
         * @return the match id
         */
        string GetMatchId();
    }
}