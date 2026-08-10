using AxShockwaveFlashObjects;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Utils;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Skua.WPF.Flash;

public class FlashUtil : IFlashUtil
{
    public FlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private AxShockwaveFlash? Flash;

    private static readonly string FlashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                               "Skua",
                                                               "flash-calls.log"
    );

    private static readonly object FlashLogLock = new();

    private static void LogFlash(string message)
    {
        try
        {
            string directory = Path.GetDirectoryName(FlashLogPath)!;
            Directory.CreateDirectory(directory);

            lock (FlashLogLock)
            {
                File.AppendAllText(
                    FlashLogPath,
                    $"{DateTime.Now:O} {message}{Environment.NewLine}"
                );
            }
        }
        catch
        {
            // O diagnóstico nunca deve interromper o cliente.
        }
    }

    public event FlashCallHandler? FlashCall;

    public void InitializeFlash()
    {
        try
        {
            if (!EoLHook.IsHooked)
                EoLHook.Hook();

            CleanupOldFlash();

            AxShockwaveFlash flash = new();
            flash.BeginInit();
            flash.Name = "flash";
            flash.Dock = DockStyle.Fill;
            flash.TabIndex = 0;
            flash.FlashCall += CallHandler;

            _messenger.Send<FlashChangedMessage<AxShockwaveFlash>>(new(flash));
            flash.EndInit();
            Flash = flash;
            byte[] swf = File.ReadAllBytes("skua.swf");
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(8 + swf.Length);
            writer.Write(1432769894);
            writer.Write(swf.Length);
            writer.Write(swf);
            writer.Seek(0, SeekOrigin.Begin);
            flash.OcxState = new AxHost.State(stream, 1, false, null);
        }
        catch (Exception ex)
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                          "Skua",
                                          "flash-init-error.log"
            );

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            File.WriteAllText(
                logPath,
                $"{DateTime.Now:O}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}"
            );

            MessageBox.Show(
                ex.ToString(),
                            "Erro real ao inicializar o Flash",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
            );

            Environment.Exit(1);
        }
        finally
        {
            EoLHook.Unhook();
        }
    }

    private void CleanupOldFlash()
    {
        if (Flash == null) return;

        Flash.FlashCall -= CallHandler;

        try
        {
            Flash.Stop();
        }
        catch { /* ignored */ }

        object ocx = null;
        try
        {
            ocx = Flash.GetOcx();
        }
        catch { /* ignored */ }

        Flash.Dispose();
        Flash = null;

        if (ocx != null)
        {
            try
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(ocx);
            }
            catch { /* ignored */ }
        }
    }


    private void CallHandler(
        object sender,
        _IShockwaveFlashEvents_FlashCallEvent e)
    {
        LogFlash($"Flash -> C# RAW: {e.request}");

        try
        {
            XElement el = XElement.Parse(e.request);
            string function = el.Attribute("name")!.Value;
            object[] args = el.Elements()
            .Select(x => FromFlashXml(x))
            .ToArray();

            LogFlash($"Flash -> C#: função '{function}'");
            LogFlash($"Disparando handlers de '{function}'");

            FlashCall?.Invoke(function, args);

            LogFlash($"Handlers de '{function}' concluídos");
        }
        catch (Exception ex)
        {
            LogFlash(
                $"ERRO ao processar Flash -> C#:{Environment.NewLine}{ex}"
            );

            throw;
        }
    }

    public string Call(string function, params object[] args)
    {
        return Call<string>(function, args);
    }

    public T Call<T>(string function, params object[] args)
    {
        try
        {
            object o = Call(function, typeof(T), args);
            return o is not null ? (T)o : (T)DefaultProvider.GetDefault<T>(typeof(T));
        }
        catch
        {
            return (T)DefaultProvider.GetDefault<T>(typeof(T));
        }
    }

    public object Call(
        string function,
        Type type,
        params object[] args)
    {
        if (_lazyManager.Value.ShouldExit &&
            Thread.CurrentThread.Name == "Script Thread")
        {
            _lazyManager.Value.ScriptCts?.Token
            .ThrowIfCancellationRequested();
        }

        try
        {
            StringBuilder req = new StringBuilder()
            .Append(
                $"<invoke name=\"{function}\" returntype=\"xml\">"
            );

            if (args.Length > 0)
            {
                req.Append("<arguments>");

                args.ForEach(
                    argument => req.Append(ToFlashXml(argument))
                );

                req.Append("</arguments>");
            }

            req.Append("</invoke>");

            string requestXml = req.ToString();

            LogFlash($"C# -> Flash: função '{function}'");
            LogFlash($"C# -> Flash XML: {requestXml}");

            string result = Flash?.CallFunction(requestXml)!;

            LogFlash(
                $"Resposta síncrona de '{function}': " +
                $"{result ?? "<null>"}"
            );

            XElement el = XElement.Parse(result);

            return el is null || el.FirstNode is null
            ? default!
            : Convert.ChangeType(
                el.FirstNode.ToString(),
                                 type
            );
        }
        catch (Exception ex)
        {
            LogFlash(
                $"ERRO C# -> Flash na função '{function}':" +
                $"{Environment.NewLine}{ex}"
            );

            _messenger.Send<FlashErrorMessage>(
                new(ex, function, args)
            );

            return default!;
        }
    }

    public static string ToFlashXml(object o)
    {
        switch (o)
        {
            case null:
                return "<null/>";

            case bool _:
                return $"<{o.ToString()!.ToLower()}/>";

            case double _:
            case float _:
            case long _:
            case int _:
                return $"<number>{o}</number>";

            case ExpandoObject _:
                StringBuilder sb = new StringBuilder().Append("<object>");
                foreach (KeyValuePair<string, object> kvp in (o as IDictionary<string, object>)!)
                    sb.Append($"<property id=\"{kvp.Key}\">{ToFlashXml(kvp.Value)}</property>");
                return sb.Append("</object>").ToString();

            default:
                if (o is Array)
                {
                    StringBuilder _sb = new StringBuilder().Append("<array>");
                    int k = 0;
                    foreach (object el in (o as Array)!)
                        _sb.Append($"<property id=\"{k++}\">{ToFlashXml(el)}</property>");
                    return _sb.Append("</array>").ToString();
                }
                return $"<string>{SecurityElement.Escape(o.ToString())}</string>";
        }
    }

    public object FromFlashXml(XElement el)
    {
        switch (el.Name.ToString())
        {
            case "number":
                return int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0;

            case "true":
                return true;

            case "false":
                return false;

            case "null":
                return null!;

            case "array":
                return el.Elements().Select(e => FromFlashXml(e)).ToArray();

            case "object":
                dynamic d = new ExpandoObject();
                el.Elements().ForEach(e => d[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First()));
                return d;

            default:
                return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    public void Dispose()
    {
        try
        {
            CleanupOldFlash();
        }
        catch { /* ignored */ }
        finally
        {
            EoLHook.Unhook();
        }
    }
}