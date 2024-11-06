using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using System.Globalization;
using Microsoft.Playwright;
using System.Text.Json;

class MovementTx
{
    const int TASK_COUNT = 2;

    static void Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        if (args.Length == 0)
        {
            HandlePath(".");
            if (Directory.GetParent("..")?.Name == "bin")
                HandlePath("../../..");  // for dev
        }
        else
            foreach (string path in args)
                HandlePath(path);
    }

    static void HandlePath(string path)
    {
        string[] files = Directory.GetFiles(path, "*.xlsx");
        foreach (string file in files)
            HandleXlsx(file);
    }

    public class TxTimestamp
    {
        public string? Address { get; set; }
        public string? Protocol { get; set; }
        public string? Txn { get; set; }
        public string? Timestamp { get; set; }
    }

    static void HandleXlsx(string path)
    {
        if (path.EndsWith(".csv"))
            return;
        string outputPath = path + ".csv";
        int row = 1;
        if (File.Exists(outputPath))
        {
            using (var stream = new StreamReader(outputPath))
            {
                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    HeaderValidated = null,
                    MissingFieldFound = null,
                    IgnoreBlankLines = true,
                    ShouldSkipRecord = (records) =>
                    {
                        // Implement logic here
                        return false;
                    }
                };
                using var reader = new CsvReader(stream, config);
                Console.WriteLine($"Trying to continue from {outputPath}");
                for (; reader.Read(); ++row)
                {
                    try
                    {
                        TxTimestamp readTag = reader.GetRecord<TxTimestamp>();
                        if (readTag.Timestamp != null && readTag.Timestamp != "")
                            continue;
                    }
                    catch (CsvHelper.ReaderException _)
                    {
                        break;
                    }
                    catch { throw; }
                }
                ++row;
            }
        }
        Console.WriteLine($"Opening input excel {path}");
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read))
        {
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                // Don't write the header again.
                HasHeaderRecord = false,
            };
            using var outStream = File.Open(outputPath, FileMode.Append);
            using var writer = new StreamWriter(outStream);
            using var csv = new CsvWriter(writer, config);
            int skipRows = row;
            Console.WriteLine($"Finding where to continue from, in {path}");
            for (; skipRows > 1 && reader.Read(); --skipRows) { }
            if (skipRows > 1)  // but finished reading source file
                return;
            Console.WriteLine($"Continue from row {row}, in {path}");
            for (; reader.Read(); ++row)
            {
                if (row == 1)
                {
                    csv.WriteHeader<TxTimestamp>();
                    csv.NextRecord();
                    continue;
                }
                string? txn = reader.GetValue(2)?.ToString();
                if (txn != null && txn.StartsWith("0x"))
                {
                    while (tasks.Count >= TASK_COUNT)
                    {// handle finished tasks
                        (Task<string> task, TxTimestamp txTimestamp) = tasks.Dequeue();
                        task.Wait();
                        string timestamp = task.Result;
                        DateTime dateTime = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(timestamp) / 1000).UtcDateTime;
                        string humanReadableDateTime = dateTime.ToString("yyyy-MM-dd HH:mm:ss");
                        Console.WriteLine($"Row {row} {txTimestamp.Txn}: {timestamp} {humanReadableDateTime}");
                        txTimestamp.Timestamp = task.Result;
                        csv.WriteRecord<TxTimestamp>(new()
                        {
                            Address = txTimestamp.Address,
                            Protocol = txTimestamp.Protocol,
                            Txn = txTimestamp.Txn,
                            Timestamp = humanReadableDateTime,
                        });
                        csv.NextRecord();
                        if ((row - tasks.Count) % 5 == 0)
                            csv.Flush();
                    }
                    tasks.Enqueue((GetTxnTimeStampUsAsync(txn), new()
                    {
                        Address = reader.GetValue(0)?.ToString(),
                        Protocol = reader.GetValue(1)?.ToString(),
                        Txn = txn,
                        Timestamp = null,
                    }));
                }
            }
            while (tasks.Count > 0)
            {
                (Task<string> task, TxTimestamp txTimestamp) = tasks.Dequeue();
                task.Wait();
                string timestamp = task.Result;
                DateTime dateTime = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(timestamp) / 1000).UtcDateTime;
                string humanReadableDateTime = dateTime.ToString("yyyy-MM-dd HH:mm:ss");
                Console.WriteLine($"Row {row} {txTimestamp.Txn}: {timestamp} {humanReadableDateTime}");
                txTimestamp.Timestamp = task.Result;
                csv.WriteRecord<TxTimestamp>(new()
                {
                    Address = txTimestamp.Address,
                    Protocol = txTimestamp.Protocol,
                    Txn = txTimestamp.Txn,
                    Timestamp = humanReadableDateTime,
                });
                csv.NextRecord();
                if ((row - tasks.Count) % 5 == 0)
                    csv.Flush();
            }
        }
    }

    static async Task<string> GetTxnTimeStampUsAsync(string mevmTxn)
    {
        while (true)
        {
            try
            {
                JsonElement response;
                response = await page.EvaluateAsync<JsonElement>($$"""
        async () => {
            return await fetch("https://mevm.devnet.imola.movementlabs.xyz/", {
            method: "POST",
            headers: {"Content-Type": "application/json",},
            body: JSON.stringify({"id":"1","jsonrpc":"2.0","method":"debug_getMoveHash","params":["{{mevmTxn}}"]}),
        }).then(r => r.ok ? r.json() : Promise.reject(r))}
        """);
                string moveTxn = response.GetProperty("result").GetString()!;
                response = await page.EvaluateAsync<JsonElement>($$"""
        async () => {
            return await fetch("https://aptos.devnet.imola.movementlabs.xyz/api/v1/transactions/by_hash/{{moveTxn}}", {
            method: "GET",
            headers: {"Content-Type": "application/json",},
        }).then(r => r.ok ? r.json() : Promise.reject(r))}
        """);
                string timestamp = response.GetProperty("timestamp").GetString()!;
                return timestamp;
            }
            catch (Exception e)
            {
                semaphore.Wait();
                try
                {
                    if (refreshing == null || refreshing.IsCompleted)
                    {
                        Console.WriteLine("Restarting browser...");
                        await browser.CloseAsync();
                        refreshing = Startup();
                    }
                    //if (refreshing == null || refreshing.IsCompleted)
                    //    refreshing = page.ReloadAsync();
                    //if (!refreshing.IsCompleted)
                    //{
                    //    await refreshing;
                    //    await page.WaitForURLAsync("https://explorer.devnet.imola.movementlabs.xyz/#/txn/0xc8fb1ec18bb97e5b2157a17a49ebdab3bb7c4b1d951c47e64c14a8b35ec076fe");
                    //    continue;
                    //}
                }
                finally { semaphore.Release(); }
                await refreshing;
                Thread.Sleep(1_000);
            }
        }
    }

    static IPlaywright playwright;
    static IBrowser browser;
    static Task? refreshing = null;
    static IPage page;
    static Queue<(Task<string>, TxTimestamp)> tasks = new(TASK_COUNT);
    private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);

    static async Task Startup()
    {
        playwright = await Playwright.CreateAsync();
        IBrowserType firefox = playwright.Firefox;
        browser = await firefox.LaunchAsync(new() { Headless = true });
        page = await browser.NewPageAsync();
        await page.RouteAsync("**/*.{png,jpg,jpeg,css,otf}", route => route.AbortAsync());
        await page.RouteAsync("https://www.google.com/*", route => route.AbortAsync());
        await page.RouteAsync("https://events.statsigapi.net/*", route => route.AbortAsync());
        await page.RouteAsync("https://vc.hotjar.io/*", route => route.AbortAsync());
        await page.RouteAsync("https://staging.aptosconnect.app/*", route => route.AbortAsync());
        // Abort based on the request type
        await page.RouteAsync("**/*", async route => {
            if ("image".Equals(route.Request.ResourceType))
                await route.AbortAsync();
            else
                await route.ContinueAsync();
        });
        await page.GotoAsync("https://explorer.devnet.imola.movementlabs.xyz/#/txn/0xc8fb1ec18bb97e5b2157a17a49ebdab3bb7c4b1d951c47e64c14a8b35ec076fe");
        page.Response += async (_, resp) =>
        {
            if (resp.Status != 200)
            {
                try
                {
                    Console.WriteLine(">> " + resp.Status + " " + resp.Request.Url + " " + resp.Request.Headers + " " + resp.Request.PostData);
                    string respText = await resp.TextAsync();
                    Console.WriteLine(respText);
                }
                catch { }
            }
        };
    }

    static MovementTx()
    {
        Task.Run(Startup);
    }
}
