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

app.MapGet("/", () => "C# SharpAstrology API is running! (Data Extraction Fixed)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        // 1. 星历文件路径
        string ephPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!Directory.Exists(ephPath)) {
            Directory.CreateDirectory(ephPath);
        }
        Environment.SetEnvironmentVariable("SE_EPHE_PATH", ephPath);
        
        // 2. 自动化云端星历文件下载器
        string[] ephFiles = { "seas_18.se1", "semo_18.se1", "sepl_18.se1" };
        using (var client = new HttpClient()) {
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0"); 
            foreach (var f in ephFiles) {
                var p = Path.Combine(ephPath, f);
                if (!File.Exists(p) || new FileInfo(p).Length < 100000) {
                    string cleanUrl = "https://" + "cdn.jsdelivr.net/gh/aloistr/swisseph@master/ephe/" + f;
                    var bytes = await client.GetByteArrayAsync(cleanUrl);
                    File.WriteAllBytes(p, bytes);
                }
            }
        }

        // 3. 星历引擎反射装载器
        Type ephType = typeof(SwissEphemerides);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object ephInstance = null;
        
        foreach (var ctor in ephType.GetConstructors(flags).OrderByDescending(c => c.GetParameters().Length)) {
            try {
                var pInfos = ctor.GetParameters();
                var argsToPass = new object[pInfos.Length];
                for (int i = 0; i < pInfos.Length; i++) {
                    var pt = pInfos[i].ParameterType;
                    if (pt.Name == "SwissEph") {
                        try { argsToPass[i] = Activator.CreateInstance(pt, ephPath); } 
                        catch { argsToPass[i] = Activator.CreateInstance(pt); }
                    } else if (pt == typeof(string)) {
                        argsToPass[i] = ephPath;
                    } else if (pt.Name.Contains("ILogger")) {
                        var loggerType = typeof(ILogger<>).MakeGenericType(ephType);
                        argsToPass[i] = services.GetService(loggerType) ?? loggerFactory.CreateLogger(ephType.Name);
                    } else {
                        var svc = services.GetService(pt);
                        if (svc != null) { argsToPass[i] = svc; } 
                        else if (pInfos[i].HasDefaultValue) { argsToPass[i] = pInfos[i].DefaultValue; } 
                        else if (pt.IsValueType) { argsToPass[i] = Activator.CreateInstance(pt); } 
                        else { argsToPass[i] = null; }
                    }
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
        
        // 5. 强类型实例化人类图
        HumanDesignChart chart = null;
        try {
            chart = new HumanDesignChart(parsedDate, eph, EphCalculationMode.Tropic);
        } catch (Exception chartEx) {
            var sigs = string.Join(" | ", ephType.GetConstructors(flags).Select(c => "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")"));
            throw new Exception($"引擎排盘计算空指针崩溃！\n内部报错: {chartEx.Message}");
        }

        // ====================================================================
        // 【新增】：核心人类图数据安全提取与翻译引擎
        // ====================================================================
        string GetProp(string propName) {
            try {
                var prop = chart.GetType().GetProperty(propName);
                return prop?.GetValue(chart)?.ToString() ?? "未知";
            } catch { return "未知"; }
        }

        var rawType = GetProp("Type");
        var rawProfile = GetProp("Profile");
        var rawAuth = GetProp("Authority");
        var rawDef = GetProp("Definition");
        var rawCross = GetProp("IncarnationCross");

        // 中文化翻译字典
        var typeDict = new Dictionary<string, string> {
            {"Manifestor", "显示者 (Manifestor)"},
            {"Generator", "生产者 (Generator)"},
            {"ManifestingGenerator", "显示生产者 (Manifesting Generator)"},
            {"Projector", "投射者 (Projector)"},
            {"Reflector", "反映者 (Reflector)"}
        };
        var authDict = new Dictionary<string, string> {
            {"Emotional", "情绪型权威"}, {"Sacral", "荐骨型权威"}, {"Splenic", "直觉型权威"},
            {"Ego", "意志力型权威"}, {"SelfProjected", "自我投射型权威"}, {"None", "无内在权威 (环境)"},
            {"Lunar", "月亮型权威"}
        };
        var defDict = new Dictionary<string, string> {
            {"None", "无定义"}, {"Single", "一分人"}, {"Split", "二分人"},
            {"TripleSplit", "三分人"}, {"QuadrupleSplit", "四分人"}
        };

        string cnType = typeDict.ContainsKey(rawType) ? typeDict[rawType] : rawType;
        string cnAuth = authDict.ContainsKey(rawAuth) ? authDict[rawAuth] : rawAuth;
        string cnDef = defDict.ContainsKey(rawDef) ? defDict[rawDef] : rawDef;

        // 自动推导策略与非自己主题
        string strategy = cnType.Contains("生产者") ? "等待回应" : 
                          cnType.Contains("显示者") ? "告知" : 
                          cnType.Contains("投射者") ? "等待被邀请" : 
                          cnType.Contains("反映者") ? "等待完整的月相周期" : "未知";

        string notSelf = cnType.Contains("生产者") ? "挫败" : 
                         cnType.Contains("显示者") ? "愤怒" : 
                         cnType.Contains("投射者") ? "苦涩" : 
                         cnType.Contains("反映者") ? "失望" : "未知";

        // ====================================================================
        // 6. 渲染 Blazor 精美图表组件
        // ====================================================================
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        var renderedHtmlString = await htmlRenderer.Dispatcher.InvokeAsync(async () => 
        {
            var dictionary = new Dictionary<string, object> { { "Chart", chart } };
            var parameters = Microsoft.AspNetCore.Components.ParameterView.FromDictionary(dictionary);
            var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);
            return output.ToHtmlString();
        });

        // 组装最终的全套数据返回给前端
        return Results.Json(new {
            success = true,
            data = new {
                name = data.Name,
                location = $"{data.Province} {data.City}".Trim(),
                type = cnType,
                profile = rawProfile.Replace("_", "/"), // 将 1_3 转为 1/3
                definition = cnDef,
                authority = cnAuth,
                strategy = strategy,
                notSelf = notSelf,
                cross = rawCross,
                chartImageSVG = renderedHtmlString
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
    public string Province { get; set; } // 新增省份
    public string City { get; set; }     // 新增城市
}
