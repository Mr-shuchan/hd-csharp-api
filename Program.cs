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
using System.Linq;
using System.Reflection;

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

app.MapGet("/", () => "C# SharpAstrology API is running! (jsDelivr CDN Active)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        // 1. 明确星历文件路径：建一个专属文件夹存放星历数据
        string ephPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!Directory.Exists(ephPath)) {
            Directory.CreateDirectory(ephPath);
        }
        Environment.SetEnvironmentVariable("SE_EPHE_PATH", ephPath);
        
        // 2. 自动化云端星历文件下载器 (使用最稳定且不限流的 jsDelivr 开源 CDN 加速节点)
        string[] ephFiles = { "seas_18.se1", "semo_18.se1", "sepl_18.se1" };
        using (var client = new HttpClient()) {
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"); 
            
            foreach (var f in ephFiles) {
                var p = Path.Combine(ephPath, f);
                // 防伪校验：如果文件不存在，或者下载成了一个小体积的 404 文本 (真实的 se1 文件远大于 100KB)
                if (!File.Exists(p) || new FileInfo(p).Length < 100000) {
                    // 拆分字符串防 Markdown 破坏
                    string cleanUrl = "https://" + "cdn.jsdelivr.net/gh/aloistr/swisseph@master/ephe/" + f;
                    
                    // 【核心修改】：不再吞噬报错！如果下载失败，直接抛出异常阻断执行
                    var bytes = await client.GetByteArrayAsync(cleanUrl);
                    File.WriteAllBytes(p, bytes);
                }
            }
        }

        // 3. 星历引擎反射装载器 (强行启动并塞入参数)
        Type ephType = typeof(SwissEphemerides);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object ephInstance = null;
        
        foreach (var ctor in ephType.GetConstructors(flags).OrderByDescending(c => c.GetParameters().Length)) {
            try {
                var pInfos = ctor.GetParameters();
                var argsToPass = new object[pInfos.Length];
                for (int i = 0; i < pInfos.Length; i++) {
                    if (pInfos[i].ParameterType == typeof(string)) argsToPass[i] = ephPath;
                    else if (pInfos[i].HasDefaultValue) argsToPass[i] = pInfos[i].DefaultValue;
                    else if (pInfos[i].ParameterType.IsValueType) argsToPass[i] = Activator.CreateInstance(pInfos[i].ParameterType);
                    else argsToPass[i] = null;
                }
                ephInstance = ctor.Invoke(argsToPass);
                break;
            } catch {}
        }
        
        if (ephInstance == null) throw new Exception("无法反射实例化 SwissEphemerides (参数签名全部不匹配)。");
        IEphemerides eph = (IEphemerides)ephInstance;
        
        // 4. 解析前端传来的时间
        DateTime parsedDate;
        if (!DateTime.TryParse($"{data.Date} {data.Time}", out parsedDate)) {
            parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        } else {
            parsedDate = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
        }
        
        // 5. 强类型实例化人类图 (日志证明此行能完美通过编译)
        var chart = new HumanDesignChart(parsedDate, eph, EphCalculationMode.Tropic);

        // 6. 渲染 Blazor 精美图表组件
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        var dictionary = new Dictionary<string, object> { { "Chart", chart } };
        
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
