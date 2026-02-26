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

app.MapGet("/", () => "C# SharpAstrology API is running! (Planets Columns Active)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    try {
        string ephPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!Directory.Exists(ephPath)) Directory.CreateDirectory(ephPath);
        
        Type ephType = typeof(SwissEphemerides);
        Type ephTypeEnum = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.Name == "EphType" && t.IsEnum);
        
        object moshierValue = null;
        if (ephTypeEnum != null) {
            try { moshierValue = Enum.Parse(ephTypeEnum, "Moshier", true); } catch {}
        }
        
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
                    } else if (ephTypeEnum != null && pt == ephTypeEnum && moshierValue != null) {
                        argsToPass[i] = moshierValue; 
                    } else if (pt == typeof(string)) {
                        argsToPass[i] = ephPath;
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

        var ccProp = chart.GetType().GetProperty("ConnectedComponents");
        var ccDict = ccProp?.GetValue(chart) as System.Collections.IDictionary;
        var definedCenters = new HashSet<string>();
        if (ccDict != null) {
            foreach (var key in ccDict.Keys) definedCenters.Add(key.ToString());
        }

        string rawAuth = "Lunar";
        if (definedCenters.Contains("SolarPlexus") || definedCenters.Contains("Solar")) rawAuth = "Emotional";
        else if (definedCenters.Contains("Sacral")) rawAuth = "Sacral";
        else if (definedCenters.Contains("Spleen")) rawAuth = "Splenic";
        else if (definedCenters.Contains("Heart") || definedCenters.Contains("Ego")) rawAuth = "Ego";
        else if (definedCenters.Contains("G")) rawAuth = "SelfProjected";
        else if (definedCenters.Contains("Head") || definedCenters.Contains("Ajna") || definedCenters.Contains("Throat")) rawAuth = "Mental";

        string GetProp(string propName) {
            try { return chart.GetType().GetProperty(propName)?.GetValue(chart)?.ToString() ?? "未知"; } 
            catch { return "未知"; }
        }

        // =================================================================================
        // 【终极更新】：提取左右两侧的红黑星历数据 (闸门与爻线)
        // =================================================================================
        var pActProp = chart.GetType().GetProperty("PersonalityActivation");
        var dActProp = chart.GetType().GetProperty("DesignActivation");
        
        var getActivationList = new Func<System.Collections.IDictionary, List<object>>(dict => {
            var list = new List<object>();
            if (dict == null) return list;
            // 人类图标准十二行星排列顺序
            string[] order = { "Sun", "Earth", "NorthNode", "SouthNode", "Moon", "Mercury", "Venus", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune", "Pluto" };
            foreach(var p in order) {
                foreach (System.Collections.DictionaryEntry kvp in dict) {
                    if (kvp.Key.ToString() == p) {
                        var act = kvp.Value;
                        // 提取闸门和爻线的整数值
                        int gateInt = Convert.ToInt32(act.GetType().GetProperty("Gate")?.GetValue(act));
                        int lineInt = Convert.ToInt32(act.GetType().GetProperty("Line")?.GetValue(act));
                        list.Add(new { planet = p, gate = gateInt, line = lineInt });
                    }
                }
            }
            return list;
        });

        var personalityPlanets = getActivationList(pActProp?.GetValue(chart) as System.Collections.IDictionary);
        var designPlanets = getActivationList(dActProp?.GetValue(chart) as System.Collections.IDictionary);

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
                type = GetProp("Type"),
                profile = GetProp("Profile").Replace("_", "/"),
                definition = GetProp("SplitDefinition"),
                authority = rawAuth,
                cross = GetProp("IncarnationCross"),
                personalityActivations = personalityPlanets, // 输出黑色显意识列表
                designActivations = designPlanets,         // 输出红色潜意识列表
                chartImageSVG = renderedHtmlString
            }
        });
    } 
    catch (Exception ex) {
        return Results.Json(new { success = false, message = "底层堆栈:\n" + ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
    public string Province { get; set; }
    public string City { get; set; }
}
