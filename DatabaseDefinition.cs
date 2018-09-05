namespace BulkQuery
{
    public class DatabaseDefinition
    {
        public ServerDefinition Server { get; }
        public string DatabaseName { get; }

        public DatabaseDefinition(string databaseName, ServerDefinition server)
        {
            DatabaseName = databaseName;
            Server = server;
        }
    }
}
