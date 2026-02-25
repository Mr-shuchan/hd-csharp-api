using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using SharpAstrology.HumanDesign.BlazorComponents;
using System.Collections.Generic;
using System;
using System.Linq;

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

app.MapGet("/", () => "C# SharpAstrology API is running! (Engine Auto-Binder Fixed)");

app.MapPost("/api/generate-chart", async (InputData data, IServiceProvider services, ILoggerFactory loggerFactory) => {
    
    try {
        await using var htmlRenderer = new HtmlRenderer(services, loggerFactory);
        
        // 1. 获取渲染组件及需要的参数类型
        var componentType = typeof(HumanDesignGraph);
        var chartProp = componentType.GetProperty("Chart") ?? 
                        componentType.GetProperties().FirstOrDefault(p => p.Name.Contains("Chart"));
        if (chartProp == null) throw new Exception("在组件中找不到 Chart 属性。");
        var chartInterfaceType = chartProp.PropertyType;
        
        // 2. 查找 HumanDesignChart 实体类
        var concreteChartType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => chartInterfaceType.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
        if (concreteChartType == null) throw new Exception("无法找到具体的排盘类！");

        // 3. 打破懒加载，强制加载星历表引擎程序集
        try {
            System.Reflection.Assembly.Load("SharpAstrology.SwissEph");
        } catch (Exception ex) {
            Console.WriteLine("强制加载程序集失败: " + ex.Message);
        }

        var ephInterface = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == "IEphemerides");

        var concreteEphType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => ephInterface != null && ephInterface.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);

        if (concreteEphType == null) {
            throw new Exception("缺少星历表引擎。请确认 AstrologyAPI.csproj 包含了 SharpAstrology.SwissEph。");
        }

        // 4. 【核心修复】：暴力智能装载星历表引擎
        object ephInstance;
        string ephPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ephe_data");
        if (!System.IO.Directory.Exists(ephPath)) {
            System.IO.Directory.CreateDirectory(ephPath);
        }

        try {
            // 第一顺位：不检查参数签名，强行将路径喂给引擎装载器，让底层的 Binder 自己去匹配（无视默认参数干扰）
            ephInstance = Activator.CreateInstance(concreteEphType, ephPath);
        } 
        catch {
            try {
                // 第二顺位兜底：尝试无参构造
                ephInstance = Activator.CreateInstance(concreteEphType);
            } catch (Exception innerEx) {
                var ctors = concreteEphType.GetConstructors().Select(c => "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)) + ")");
                throw new Exception($"星历表引擎 {concreteEphType.Name} 暴力装载彻底失败。可用构造: {string.Join(" | ", ctors)}。内部报错: {innerEx.InnerException?.Message ?? innerEx.Message}");
            }
        }

        // 5. 查找计算模式枚举
        var modeEnum = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == "EphCalculationMode" && t.IsEnum);

        var modeValue = modeEnum != null ? Enum.GetValues(modeEnum).GetValue(0) : 0;

        // 6. 实例化排盘对象
        object chartInstance;
        try {
            // 解析用户传来的时间 (需要将前端传来的时间格式化为正确的 DateTime)
            // 这里我们先用硬编码时间测试连通性，跑通后你再替换为真实的 data.Date 解析
            var parsedDate = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            chartInstance = Activator.CreateInstance(concreteChartType, parsedDate, ephInstance, modeValue);
        } catch (Exception ex) {
            throw new Exception($"传入参数实例化排盘实体失败。报错详情: {ex.InnerException?.Message ?? ex.Message}");
        }

        // 7. 渲染精美图表
        var chartHtmlString = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var dictionary = new Dictionary<string, object> { { chartProp.Name, chartInstance } };
            var parameters = ParameterView.FromDictionary(dictionary);
            
            var output = await htmlRenderer.RenderComponentAsync<HumanDesignGraph>(parameters);
            return output.ToHtmlString();
        });

        return Results.Json(new {
            success = true,
            data = new {
                name = data.Name,
                chartImageSVG = chartHtmlString 
            }
        });
    } 
    catch (Exception ex) {
        return Results.Json(new { success = false, message = ex.ToString() });
    }
});

app.Run();

public class InputData {
    public string Name { get; set; }
    public string Date { get; set; }
    public string Time { get; set; }
}
