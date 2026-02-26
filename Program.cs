using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
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
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddLogging();
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(); 

var app = builder.Build();
app.UseCors();

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        string ephPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!Directory.Exists(ephPath)) Directory.CreateDirectory(ephPath);
        Environment.SetEnvironmentVariable("SE_EPHE_PATH", ephPath);
        
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
                    } else if (pt == typeof(string)) { argsToPass[i] = ephPath;
                    } else if (pt.Name.Contains("ILogger")) {
                        var loggerType = typeof(ILogger<>).MakeGenericType(ephType);
                        argsToPass[i] = services.GetService(loggerType) ?? loggerFactory.CreateLogger(ephType.Name);
                    } else {
                        var svc = services.GetService(pt);
                        if (svc != null) argsToPass[i] = svc;
                        else if (pInfos[i].HasDefaultValue) argsToPass[i] = pInfos[i].DefaultValue;
                        else if (pt.IsValueType) argsToPass[i] = Activator.CreateInstance(pt);
                        else argsToPass[i] = null;
                    }
                }
                ephInstance = ctor.Invoke(argsToPass);
                break;
            } catch {}
        }
        
        if (ephInstance == null) throw new Exception("无法反射实例化 SwissEphemerides");
        IEphemerides eph = (IEphemerides)ephInstance;
        
        DateTime parsedDate;
        if (!DateTime.TryParse($"{data.Date} {data.Time}", out parsedDate)) {
            parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        } else {
            parsedDate = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
        }
        
        var chart = new HumanDesignChart(parsedDate, eph, EphCalculationMode.Tropic);

        // 【超级诊断器】：强制获取所有属性！看看权威和定义到底被作者改成了什么名字！
        var debugProps = new Dictionary<string, string>();
        foreach (var p in chart.GetType().GetProperties()) {
            try { debugProps[p.Name] = p.GetValue(chart)?.ToString() ?? "null"; } catch {}
        }
        
        // 智能模糊匹配多个可能的属性名
        var rawType = debugProps.GetValueOrDefault("Type", "未知");
        var rawProfile = debugProps.GetValueOrDefault("Profile", "未知");
        var rawAuth = debugProps.GetValueOrDefault("Authority", debugProps.GetValueOrDefault("InnerAuthority", "未知"));
        var rawDef = debugProps.GetValueOrDefault("Definition", debugProps.GetValueOrDefault("SplitDefinition", "未知"));
        var rawCross = debugProps.GetValueOrDefault("IncarnationCross", "未知");

        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        var renderedHtmlString = await htmlRenderer.Dispatcher.InvokeAsync(async () => 
        {
            var dictionary = new Dictionary<string, object> { { "Chart", chart } };
            var parameters = Microsoft.AspNetCore.Components.ParameterView.FromDictionary(dictionary);
            var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);
            return output.ToHtmlString();
        });

        return Results.Json(new {
            success = true,
            data = new {
                name = data.Name,
                location = $"{data.Province} {data.City}".Trim(),
                parsedDateTime = parsedDate.ToString("yyyy-MM-dd HH:mm:ss"), // 把后端真实计算时间传给前端对账
                rawDebugProps = debugProps, // 抛出字典
                type = rawType,
                profile = rawProfile.Replace("_", "/"),
                definition = rawDef,
                authority = rawAuth,
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
public class InputData { public string Name { get; set; } public string Date { get; set; } public string Time { get; set; } public string Province { get; set; } public string City { get; set; } }
