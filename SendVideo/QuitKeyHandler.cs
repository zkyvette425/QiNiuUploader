namespace SendVideo;

public class QuitKeyHandler
{
    public void StartListening(Action exitAction)
    {
        while (true)
        {
            if (Console.KeyAvailable)
            {
                var keyInfo = Console.ReadKey(true); // 读取按键并隐藏显示

                // 检测 Shift+Alt+Q 组合键（不区分大小写，Q/q 均可）
                if (keyInfo.Modifiers == (ConsoleModifiers.Shift | ConsoleModifiers.Alt) &&
                    (keyInfo.Key == ConsoleKey.Q || keyInfo.Key == ConsoleKey.Q))
                {
                    exitAction?.Invoke();
                }
            }
            Thread.Sleep(100); // 降低 CPU 占用
        }
    }
}