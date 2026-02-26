using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

// 引入全套官方命名空间
using SharpAstrology.DataModels;
using SharpAstrology.Interfaces;
using SharpAstrology.Enums;
using SharpAstrology.Ephemerides;
using SharpAstrology.HumanDesign.BlazorComponents;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// 配置跨域
builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddLogging();
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(); 

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => "C# SharpAstrology API is running! (Astro.com Engine Active)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        // 1. 明确星历文件路径：直接放在运行根目录，SwissEph底层默认会在当前或环境变量目录寻找
        string ephPath = AppDomain.CurrentDomain.BaseDirectory;
        Environment.SetEnvironmentVariable("SE_EPHE_PATH", ephPath);
        
        // 2. 自动化云端星历文件下载器 (切换为最稳定、绝对正确的官方 astro.com 源)
        string[] ephFiles = { "seas_18.se1", "semo_18.se1", "sepl_18.se1" };
        using (var client = new HttpClient()) {
            // 增加 UserAgent 防止被服务器拦截下载假文件
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"); 
            
            foreach (var f in ephFiles) {
                var p = Path.Combine(ephPath, f);
                // 防伪校验：如果文件不存在，或者下载成了只有几百字节的报错文本，则重新下载
                if (!File.Exists(p) || new FileInfo(p).Length < 1000) {
                    string cleanUrl = "https://" + "www.astro.com/ftp/swisseph/ephe/" + f;
                    try {
                        var bytes = await client.GetByteArrayAsync(cleanUrl);
                        File.WriteAllBytes(p, bytes);
                    } catch (Exception ex) {
                        Console.WriteLine($"文件 {f} 下载失败: {ex.Message}");
                    }
                }
            }
        }

        // 3. 强类型实例化星历引擎 (彻底告别反射，编译器打包完美支持)
        IEphemerides eph = new SwissEphemerides();
        
        // 4. 解析前端传来的时间
        DateTime parsedDate;
        if (!DateTime.TryParse($"{data.Date} {data.Time}", out parsedDate)) {
            // 如果前端时间格式错误，兜底使用 2000 年作为演示
            parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        } else {
            // 确保标记为 UTC 时间
            parsedDate = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
        }
        
        // 5. 实例化人类图 (使用从报错中确认的 Tropic 回归线枚举模式)
        var chart = new HumanDesignChart(parsedDate, eph, EphCalculationMode.Tropic);

        // 6. 渲染 Blazor 精美图表组件
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        var dictionary = new Dictionary<string, object> { { "Chart", chart } };
        
        // 明确指定命名空间防止 CS0103 错误
        var parameters = Microsoft.AspNetCore.Components.ParameterView.FromDictionary(dictionary);
        var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);

        return Results.Json(new {
            success = true,
            data = new {
                name = data.Name,
                chartImageSVG = output.ToHtmlString()
            }
        });
    } 
    catch (Exception ex) {
        return Results.Json(new { success = false, message = "底层完整堆栈:\n" + ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
