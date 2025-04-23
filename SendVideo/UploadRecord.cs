
using System.Data.SQLite;

namespace SendVideo;

public class VideoRecord
{
    public string FileName { get; set; }
    public string Status { get; set; }
    public DateTime UploadDate { get; set; }
    public bool IsLocalDeleted { get; set; }
}

public class UploadRecord
{
    private readonly string _connectionString;

    public UploadRecord(string databasePath)
    {
        string directory = Path.GetDirectoryName(databasePath);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = $"Data Source={databasePath};Version=3;";
        CreateTableIfNotExists();
    }

    private void CreateTableIfNotExists()
    {
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string createTableQuery = @"
                CREATE TABLE IF NOT EXISTS VideoRecords (
                    FileName TEXT PRIMARY KEY,
                    Status TEXT,
                    UploadDate DATETIME,
                    IsLocalDeleted BOOLEAN
                )";
        using var command = new SQLiteCommand(createTableQuery, connection);
        command.ExecuteNonQuery();
    }

    public void AddNewFile(string fileName)
    {
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string insertQuery = @"
                INSERT OR IGNORE INTO VideoRecords (FileName, Status, UploadDate, IsLocalDeleted)
                VALUES (@FileName, '未上传', NULL, 0)";
        using var command = new SQLiteCommand(insertQuery, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.ExecuteNonQuery();
    }

    public List<VideoRecord> GetInterruptedFiles()
    {
        var records = new List<VideoRecord>();
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string selectQuery = "SELECT * FROM VideoRecords WHERE Status = '中断上传' AND IsLocalDeleted = 0";
        using var command = new SQLiteCommand(selectQuery, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new VideoRecord
            {
                FileName = reader.GetString(0),
                Status = reader.GetString(1),
                UploadDate = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                IsLocalDeleted = reader.GetBoolean(3)
            });
        }

        return records;
    }

    public List<VideoRecord> GetNotUploadedFiles()
    {
        var records = new List<VideoRecord>();
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string selectQuery = "SELECT * FROM VideoRecords WHERE Status = '未上传' AND IsLocalDeleted = 0";
        using var command = new SQLiteCommand(selectQuery, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new VideoRecord
            {
                FileName = reader.GetString(0),
                Status = reader.GetString(1),
                UploadDate = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                IsLocalDeleted = reader.GetBoolean(3)
            });
        }

        return records;
    }

    public List<VideoRecord> GetAllFiles()
    {
        var records = new List<VideoRecord>();
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string selectQuery = "SELECT * FROM VideoRecords";
        using var command = new SQLiteCommand(selectQuery, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new VideoRecord
            {
                FileName = reader.GetString(0),
                Status = reader.GetString(1),
                UploadDate = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                IsLocalDeleted = reader.GetBoolean(3)
            });
        }

        return records;
    }

    public void MarkFileAsUploading(string fileName)
    {
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string updateQuery = "UPDATE VideoRecords SET Status = '上传中' WHERE FileName = @FileName";
        using var command = new SQLiteCommand(updateQuery, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.ExecuteNonQuery();
    }

    public void MarkFileAsUploaded(string fileName)
    {
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string updateQuery = "UPDATE VideoRecords SET Status = '已上传', UploadDate = @UploadDate WHERE FileName = @FileName";
        using var command = new SQLiteCommand(updateQuery, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@UploadDate", DateTime.Now);
        command.ExecuteNonQuery();
    }

    public void MarkFileAsInterrupted(string fileName)
    {
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string updateQuery = "UPDATE VideoRecords SET Status = '中断上传' WHERE FileName = @FileName";
        using var command = new SQLiteCommand(updateQuery, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.ExecuteNonQuery();
    }

    public void MarkFileAsLocalDeleted(string fileName)
    {
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string updateQuery = "UPDATE VideoRecords SET IsLocalDeleted = 1 WHERE FileName = @FileName";
        using var command = new SQLiteCommand(updateQuery, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.ExecuteNonQuery();
    }

    public List<VideoRecord> GetUploadedFilesOlderThanDays(int days)
    {
        var records = new List<VideoRecord>();
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        string selectQuery = $"SELECT * FROM VideoRecords WHERE Status = '已上传' AND UploadDate < @DateThreshold AND IsLocalDeleted = 0";
        using var command = new SQLiteCommand(selectQuery, connection);
        command.Parameters.AddWithValue("@DateThreshold", DateTime.Now.AddDays(-days));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new VideoRecord
            {
                FileName = reader.GetString(0),
                Status = reader.GetString(1),
                UploadDate = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                IsLocalDeleted = reader.GetBoolean(3)
            });
        }

        return records;
    }
    
    public bool HasFileInUploadingStatus()
    {
        using var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        // 修改表名从 UploadRecords 为 VideoRecords
        string query = "SELECT COUNT(*) FROM VideoRecords WHERE Status = '上传中'";
        using var command = new SQLiteCommand(query, connection);
        int count = Convert.ToInt32(command.ExecuteScalar());
        return count > 0;
    }
}