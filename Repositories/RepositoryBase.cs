using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SqlClient;
using System.Configuration;

namespace WPFApp.Repositories
{
    public class RepositoryBase
    {
        private readonly string _connectionString;
        
        public RepositoryBase()
        {
            // _connectionString = Environment.GetEnvironmentVariable("AZURE_CONNECTION_STRING");
            // _connectionString = Environment.GetEnvironmentVariable("Server=MUHAMMAD-MAIZ-N\\MSSQLSERVER22; Database=BloodBank; Integrated Security=true");
            // _connectionString = ConfigurationManager.ConnectionStrings["AzureDbConnectionString"].ConnectionString;
            _connectionString = "";
        }
        
        public SqlConnection GetConnection()
        {
            return new SqlConnection(_connectionString);
        }
    }
}
