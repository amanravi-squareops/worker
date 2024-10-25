using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using StackExchange.Redis;
using DotNetEnv;

namespace Worker
{
    public class Program
    {
        private static string _databaseName; // Declare databaseName at class level

        public static int Main(string[] args)
        {
            try
            {
                // Load environment variables from .env file
                DotNetEnv.Env.Load();

                _databaseName = Environment.GetEnvironmentVariable("DB_NAME"); // Assign value to _databaseName

                var redisHostname = Environment.GetEnvironmentVariable("REDIS_HOSTNAME");
                var redisConn = OpenRedisConnection(redisHostname);
                var redis = redisConn.GetDatabase();
                var dbServer = Environment.GetEnvironmentVariable("DB_SERVER");
                var dbUsername = Environment.GetEnvironmentVariable("DB_USERNAME");
                var dbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD");
                var hostname = Environment.GetEnvironmentVariable("REDIS_HOST");

                Console.WriteLine($"REDIS_HOSTNAME: {redisHostname}");

                var pgsql = OpenDbConnection($"Server={dbServer};Username={dbUsername};Password={dbPassword};Database={_databaseName}"); // Use _databaseName

                // Keep alive is not implemented in Npgsql yet. This workaround was recommended:
                // https://github.com/npgsql/npgsql/issues/1214#issuecomment-235828359
                var keepAliveCommand = pgsql.CreateCommand();
                keepAliveCommand.CommandText = "SELECT 1";

                var definition = new { vote = "", voter_id = "" };
                while (true)
                {
                    // Slow down to prevent CPU spike, only query each 100ms
                    Thread.Sleep(100);

                    // Reconnect redis if down
                    if (redisConn == null || !redisConn.IsConnected)
                    {
                        Console.WriteLine("Reconnecting Redis");
                        redisConn = OpenRedisConnection(redisHostname);
                        redis = redisConn.GetDatabase();
                    }
                    string json = redis.ListLeftPopAsync("votes").Result;
                    if (json != null)
                    {
                        var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                        Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");
                        // Reconnect DB if down
                        if (!pgsql.State.Equals(System.Data.ConnectionState.Open))
                        {
                            Console.WriteLine("Reconnecting DB");
                            pgsql = OpenDbConnection($"Server={dbServer};Username={dbUsername};Password={dbPassword};Database={_databaseName}"); // Use _databaseName
                        }
                        else
                        { // Normal +1 vote requested
                            UpdateVote(pgsql, vote.voter_id, vote.vote);
                        }
                    }
                    else
                    {
                        keepAliveCommand.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        // Rest of your methods...

        private static NpgsqlConnection OpenDbConnection(string connectionString)
        {
            NpgsqlConnection connection = null;

            while (true)
            {
                try
                {
                    connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    Console.WriteLine("Connected to PostgreSQL.");
                    // Inside the OpenDbConnection method, before executing the SQL command
                    Console.WriteLine($"Database name first: {_databaseName}");
                    
                    // Ensure that the votes table exists
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = $"CREATE TABLE IF NOT EXISTS votes (id VARCHAR(255) NOT NULL UNIQUE,vote VARCHAR(255) NOT NULL)"; // Use _databaseName
                        Console.WriteLine($"Database name: {_databaseName}");
                        Console.WriteLine($"SQL statement: {command.CommandText}");
                        command.ExecuteNonQuery();
                        Console.WriteLine($"Database '{_databaseName}' created or already exists."); // Use _databaseName
                    }

                    break;
                }
                catch (SocketException)
                {
                    Console.Error.WriteLine("Waiting for PostgreSQL.");
                    Thread.Sleep(1000);
                }
                catch (DbException ex)
                {
                    Console.Error.WriteLine($"Error connecting to PostgreSQL: {ex.Message}");
                    Console.Error.WriteLine($"Connection string: {connectionString}");
                    Thread.Sleep(1000);
                }
            }

            return connection;
        }

      private static ConnectionMultiplexer OpenRedisConnection(string hostname)
        {
            string password = Environment.GetEnvironmentVariable("REDIS_PASSWORD"); // Assuming password is optional

            var configurationOptions = new ConfigurationOptions
            {
                EndPoints = { hostname },
                Password = password,
                AbortOnConnectFail = false,
                // Additional configuration options as needed
            };

            Console.WriteLine($"Connecting to Redis at {hostname}");
            while (true)
            {
                try
                {
                    return ConnectionMultiplexer.Connect(configurationOptions);
                }
                catch (RedisConnectionException)
                {
                    Console.Error.WriteLine("Waiting for Redis, retrying...");
                    Thread.Sleep(1000); // Consider implementing a more sophisticated retry logic
                }
            }
        }

        private static void UpdateVote(NpgsqlConnection connection, string voterId, string vote)
        {
            var command = connection.CreateCommand();
            try
            {
                command.CommandText = @"
                    INSERT INTO votes (id, vote) 
                    VALUES (@id, @vote) 
                    ON CONFLICT (id) DO UPDATE SET vote = @vote";
                
                command.Parameters.AddWithValue("@id", voterId);
                command.Parameters.AddWithValue("@vote", vote);
                
                command.ExecuteNonQuery();
                Console.WriteLine("Vote updated in PostgreSQL.");
            }
            catch (DbException ex)
            {
                Console.Error.WriteLine($"Error updating vote in PostgreSQL: {ex.Message}");
            }
            finally
            {
                command.Dispose();
            }
        }
    }
}
