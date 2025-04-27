using Qiniu.Http;
namespace SendVideo;

public class FileMonitor
{
    private readonly string _gameRecordingFolder;
    private readonly string _videoFolder;
    private readonly int _checkIntervalSeconds;
    private readonly Uploader _uploader;
    private readonly UploadRecord _record;
    private readonly object _fileOperationLock = new object();

    public FileMonitor()
    {
        _gameRecordingFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "游戏录制"
        );
        _videoFolder = Path.Combine(_gameRecordingFolder, "视频");
        _checkIntervalSeconds = AppSettings.CheckIntervalSeconds;
        _record = new UploadRecord(Path.Combine(_gameRecordingFolder, "record.db"));
        _uploader = new Uploader(AppSettings.AccessKey, AppSettings.SecretKey, AppSettings.Bucket);
        ResetStuckUploadingStatusOnStartup();
    }

    public void StartMonitoring()
    {
        while (true)
        {
            try
            {
                var folder = FoldersExist();
                if (!string.IsNullOrEmpty(folder))
                {
                    Console.WriteLine($"等待其他程序创建文件夹:{folder}，将于 {_checkIntervalSeconds} 秒后重试");
                    Thread.Sleep(_checkIntervalSeconds * 1000);
                    continue;
                }

                UpdateDatabaseWithNewFiles();
                UploadFilesBasedOnDatabaseStatus();
                DeleteOldUploadedFiles();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"监控过程中出现错误: {ex.Message}");
            }

            Thread.Sleep(_checkIntervalSeconds * 1000);
        }
    }

    private string? FoldersExist()
    {
        if (!Directory.Exists(_gameRecordingFolder))
        {
            return _gameRecordingFolder;
        }
        if (!Directory.Exists(_videoFolder))
        {
            return _videoFolder;
        }
        return null;
    }

    private void UpdateDatabaseWithNewFiles()
    {
        lock (_fileOperationLock)
        {
            var existingFiles = _record.GetAllFiles().Select(f => f.FileName).ToList();
            var candidateFiles = Directory.GetFiles(_videoFolder)
                .Select(Path.GetFileName)
                .Where(f => !existingFiles.Contains(f) && !f.EndsWith(".progress", StringComparison.OrdinalIgnoreCase));

            foreach (var candidateFile in candidateFiles)
            {
                string filePath = Path.Combine(_videoFolder, candidateFile);
                // 检查文件是否可正常读取（避免处理未粘贴完成的文件）
                if (IsFileReadyForProcessing(filePath))
                {
                    if (IsValidFileNameFormat(candidateFile))
                    {
                        _record.AddNewFile(candidateFile);
                        Console.WriteLine($"已将新文件 {candidateFile} 添加到数据库，状态为未上传");
                    }
                    else
                    {
                        DeleteInvalidFile(filePath);
                    }
                }
            }
        }
    }
    
    private bool IsValidFileNameFormat(string fileName)
    {
        var parts = fileName.Split('_', 2);
        return parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]);
    }

    private void DeleteInvalidFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
            Console.WriteLine($"删除不符合格式的文件: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"删除文件 {filePath} 时出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查文件是否可读写（非锁定状态，粘贴已完成）
    /// </summary>
    private bool IsFileReadyForProcessing(string filePath)
    {
        try
        {
            // 尝试以独占模式打开文件，若成功则文件未被占用（粘贴完成）
            using FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // 文件被占用（粘贴中），暂不处理
            return false;
        }
    }

    private void UploadFilesBasedOnDatabaseStatus()
    {
        lock (_fileOperationLock)
        {
            // 仅当存在 **有效** 上传中文件时（文件和进度文件都存在），才等待
            if (HasValidUploadingFiles())
            {
                Console.WriteLine("有文件正在有效续传，等待上传完成...");
                return;
            }
            
            // 优先处理中断上传的文件
            var interruptedFiles = _record.GetInterruptedFiles();
            foreach (var file in interruptedFiles)
            {
                string filePath = Path.Combine(_videoFolder, file.FileName);
                HandleFile(filePath, file.FileName, "中断上传");
            }

            // 处理未上传的文件
            var notUploadedFiles = _record.GetNotUploadedFiles();
            foreach (var file in notUploadedFiles)
            {
                string filePath = Path.Combine(_videoFolder, file.FileName);
                HandleFile(filePath, file.FileName, "未上传");
            }
        }
    }
    
    // 新增：检查是否存在有效上传中文件（文件和进度文件均存在）
    private bool HasValidUploadingFiles()
    {
        var uploadingFiles = _record.GetAllFiles().Where(f => f.Status == "上传中").ToList();
        return uploadingFiles.Any(f => 
            File.Exists(Path.Combine(_videoFolder, f.FileName)) && 
            File.Exists(Path.Combine(_videoFolder, $"{f.FileName}.progress"))
        );
    }
    
    /// <summary>
    /// 清理无效的“上传中”状态（文件不存在或进度文件丢失）
    /// </summary>
    private void CleanInvalidUploadingStatus()
    {
        var uploadingFiles = _record.GetAllFiles().Where(f => f.Status == "上传中").ToList();
        foreach (var file in uploadingFiles)
        {
            string filePath = Path.Combine(_videoFolder, file.FileName);
            string progressFilePath = Path.Combine(_videoFolder, $"{file.FileName}.progress");

            // 若文件不存在或进度文件不存在，视为中断
            if (!File.Exists(filePath) || !File.Exists(progressFilePath))
            {
                _record.MarkFileAsInterrupted(file.FileName);
                Console.WriteLine($"清理无效的上传中状态：{file.FileName}");
            }
        }
    }
    
    private void ResetStuckUploadingStatusOnStartup()
    {
        lock (_fileOperationLock)
        {
            var uploadingFiles = _record.GetAllFiles().Where(f => f.Status == "上传中").ToList();
            foreach (var file in uploadingFiles)
            {
                _record.MarkFileAsInterrupted(file.FileName);
                Console.WriteLine($"启动时将上传中状态的文件 {file.FileName} 重置为上传中断");
            }
        }
    }

    private void HandleFile(string filePath, string fileName, string expectedStatus)
    {
        if (!File.Exists(filePath))
        {
            _record.MarkFileAsLocalDeleted(fileName);
            return;
        }

        var fileStatus = _record.GetAllFiles().FirstOrDefault(f => f.FileName == fileName)?.Status;
        if (fileStatus == expectedStatus)
        {
            TryUploadFile(filePath, fileName);
        }
    }

    private void TryUploadFile(string filePath, string key)
    {
        string originalKey = key;
        int dotIndex = originalKey.LastIndexOf('.');
        string baseName = dotIndex > 0 ? originalKey.Substring(0, dotIndex) : originalKey;
        string extension = dotIndex > 0 ? originalKey.Substring(dotIndex) : "";
    
        string[] parts = baseName.Split('_', 2);
        if (parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]))
        {
            key = $"{parts[0]}/{parts[1]}{extension}"; // 保留扩展名（如 A/B.ext）
        }
        else
        {
            // 无效格式，按需求处理（如记录错误，不上传）
            Console.WriteLine($"无效文件名格式 {originalKey}，应为 A_B.ext");
            return;
        }
        int maxRetries = 3;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                // 在上传文件前打印文件名称
                Console.WriteLine($"即将开始上传文件: {originalKey}，上传的 key 为: {key}");

                _record.MarkFileAsUploading(originalKey);
                HttpResult result = _uploader.UploadFile(filePath,originalKey,key);

                if (result.Code == 200)
                {
                    _record.MarkFileAsUploaded(originalKey);
                    DeleteProgressFile(originalKey);
                    Console.WriteLine($"文件 {originalKey} 上传成功，已删除对应的 .progress 文件");
                }
                else
                {
                    _record.MarkFileAsInterrupted(originalKey);
                }
                Console.WriteLine($"文件 {originalKey} 上传结果: {result.ToString()}");
                return;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"上传 {filePath} 时出现 I/O 异常，重试 {retry + 1}/{maxRetries}: {ex.Message}");
                if (retry == maxRetries - 1)
                {
                    _record.MarkFileAsInterrupted(originalKey);
                    Console.WriteLine($"文件 {originalKey} 上传失败，达到最大重试次数。");
                }
            }
            catch (Exception ex)
            {
                _record.MarkFileAsInterrupted(originalKey);
                Console.WriteLine($"文件 {originalKey} 上传失败: {ex.Message}");
            }
        }
    }

    private void DeleteProgressFile(string key)
    {
        string progressFilePath = Path.Combine(_videoFolder, $"{key}.progress");
        if (File.Exists(progressFilePath))
        {
            try
            {
                File.Delete(progressFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"删除 {progressFilePath} 时出错: {ex.Message}");
            }
        }
    }

    private void DeleteOldUploadedFiles()
    {
        lock (_fileOperationLock)
        {
            var oldFiles = _record.GetUploadedFilesOlderThanDays(7);
            foreach (var file in oldFiles)
            {
                string filePath = Path.Combine(_videoFolder, file.FileName);
                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                        _record.MarkFileAsLocalDeleted(file.FileName);
                        Console.WriteLine($"已删除本地旧文件: {file.FileName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"删除文件 {file.FileName} 时出错: {ex.Message}");
                    }
                }
                else
                {
                    _record.MarkFileAsLocalDeleted(file.FileName);
                }
            }
        }
    }
}
    