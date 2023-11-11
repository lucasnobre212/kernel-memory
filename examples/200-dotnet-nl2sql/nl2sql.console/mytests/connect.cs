using System;
using MySql.Data.MySqlClient;

namespace MySqlConnectivityTest
{
    class Program
    {
        static void Main(string[] args)
        {
            string connectionString = "server=localhost;port=3306;database=test;user=root;password=example";

            try
            {
                using (var connection = new MySqlConnection(connectionString))
                {
                    connection.Open();
                    Console.WriteLine("Connection successful!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred while connecting to the database:");
                Console.WriteLine(ex.Message);
            }

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}
