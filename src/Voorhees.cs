/*----------------------------------------------------------------------------*/
/* Voorhees.cs                                                                */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* Voorhees is a hierarchical editor for Windows: JSON, INI, Klipper /        */
/* Moonraker / KlipperScreen config and registry (.reg) files, opened and     */
/* saved losslessly.  ("Jason" Voorhees; JSON.  Get it?)                      */
/*                                                                            */
/* This file is the program's front end: the process entry point, the main    */
/* editor window (MainForm), its dialogs (Find / Replace, Set as Default,     */
/* Colors) and the About box.  A document is shown as a tree on the left,     */
/* with an edit panel on the right for the selected entry's key, type,        */
/* value, line comment and raw text.                                          */
/*                                                                            */
/* Source files, each named explicitly in compile.bat:                        */
/*     src\Voorhees.cs          : this file.                                  */
/*     src\VoorheesDocument.cs  : the document model, its changes, undo and   */
/*                                redo, search and replace, and the editor    */
/*                                actions that build changes.                 */
/*     src\VoorheesFormat.cs    : the format dispatcher: every                */
/*                                format-dependent question, answered by the  */
/*                                format's module.                            */
/*     src\VoorheesText.cs      : file bytes to text and back (encodings,     */
/*                                byte order marks), line-ending detection.   */
/*     src\VoorheesJson.cs      : the lossless JSON reader and writer         */
/*                                (comments allowed).                         */
/*     src\VoorheesLines.cs     : the line engine shared by INI, the Klipper  */
/*                                family and REG.                             */
/*     src\VoorheesIni.cs       : INI files.                                  */
/*     src\VoorheesCfg.cs       : Klipper, Moonraker and KlipperScreen        */
/*                                configs.                                    */
/*     src\VoorheesReg.cs       : registry (.reg) files.                      */
/*     src\VoorheesTreeView.cs  : the tree control: built lazily, updated in  */
/*                                place.                                      */
/*     src\Version.cs           : the version string; written by the build.   */
/*     src\lib\BusyModalForm.cs : the progress dialog.                        */
/*                                                                            */
/* The program starts as a command line tool (Program.Main): it can check a   */
/* file or set up Explorer integration and exit, or open the editor.          */
/*                                                                            */
/* How the editor hangs together:                                             */
/*     o The document is a VoorheesDocument: Voorhees' own lossless model,    */
/*       read from the file on load and written back only on save.  Saving    */
/*       an unedited file writes it back byte for byte.                       */
/*     o Every edit is an editor action on the document (Vd_Act...), made     */
/*       inside one undo step.  The step's changes are then applied to the    */
/*       tree one by one (DOC_ApplyStepToTree), so the tree is never rebuilt. */
/*     o Undo and redo replay the recorded changes backwards and forwards;    */
/*       the history lasts until the next save.                               */
/*     o Edits typed into the panel are not in the document until Save is     */
/*       pressed.  Anything that would reload the panel asks about them       */
/*       first (EDIT_ConfirmPendingPanel).                                    */
/*                                                                            */
/* Window placement, splitter ratio, font size and the raw-text view option   */
/* are remembered in the registry under HKCU\Software\Voorhees.               */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Voorhees.Forms;
using Microsoft.Win32;

/*----------------------------------------------------------------------------*/
/* Program: process entry point                                               */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* Program                                                                    */
/*                                                                            */
/* Voorhees starts as a command line program and becomes a window only        */
/* when no command was asked for.  Main attaches to the console it was        */
/* started from (if any) before anything else, reads every argument,          */
/* and then either runs a command and exits, or detaches from the             */
/* console and opens the editor.  Functions are grouped by prefix:            */
/*     APP_ : command line parsing, the commands, and the debug log.          */
/*                                                                            */
/* Command line (APP_PrintUsage prints it):                                   */
/*     Voorhees [--debug] [file.json]          the editor                     */
/*     Voorhees --validate file.json [--verbose]                              */
/*     Voorhees --register   [--verbose]                                      */
/*     Voorhees --unregister [--verbose]                                      */
/*     Voorhees --version                                                     */
/*     Voorhees --help                                                        */
/*                                                                            */
/* The commands report failures only, on stderr, plus their exit code         */
/* (APP_EXIT_ values); --verbose adds a line saying what succeeded.           */
/*----------------------------------------------------------------------------*/
static class Program
{
    /*----------------------------------------------------------------------*/
    /* Exit codes.  Main returns one of these from every command; the       */
    /* editor itself always exits with APP_EXIT_OK.                         */
    /*----------------------------------------------------------------------*/

    public const int APP_EXIT_OK = 0;                    // success
    public const int APP_EXIT_INVALID = 1;               // --validate: the file is not valid JSON
    public const int APP_EXIT_USAGE = 2;                 // the command line could not be understood
    public const int APP_EXIT_UNREADABLE = 3;            // the file could not be read
    public const int APP_EXIT_REGFAILED = 4;             // --register / --unregister: the registry refused

    /*----------------------------------------------------------------------*/
    /* Commands Main can run instead of opening the editor.                 */
    /*----------------------------------------------------------------------*/

    const string APP_CMD_HELP = "help";                  // --help, -h, /?
    const string APP_CMD_VERSION = "version";            // --version
    const string APP_CMD_VALIDATE = "validate";          // --validate file
    const string APP_CMD_REGISTER = "register";          // --register
    const string APP_CMD_UNREGISTER = "unregister";      // --unregister
    const string APP_CMD_SETDEFAULT = "set-default";     // --set-default ext[,ext...]
    const string APP_CMD_UNSETDEFAULT = "unset-default"; // --unset-default ext[,ext...]

    /*----------------------------------------------------------------------*/
    /* Win32 values used to share and then leave the starting console.      */
    /*----------------------------------------------------------------------*/

    const int APP_ATTACH_PARENT_PROCESS = -1;            // AttachConsole: the console of the process that started us
    const uint APP_WM_KEYDOWN = 0x0100;                  // window message: key pressed
    const uint APP_WM_KEYUP = 0x0101;                    // window message: key released
    const int APP_VK_RETURN = 0x0D;                      // virtual key code of Enter

    /*----------------------------------------------------------------------*/
    /* Debug log file, written only with --debug.  It lives in a log        */
    /* folder beside the exe, so it travels with the program rather than    */
    /* landing in a per-user folder.                                        */
    /*----------------------------------------------------------------------*/

    const string APP_LOGDIR = "log";                     // folder beside Voorhees.exe
    const string APP_LOGFILE = "Voorhees_debug.log";     // appended to, one line per entry

    /*----------------------------------------------------------------------*/
    /* Settings from the command line.  Read by MainForm as well, which     */
    /* is why the mode flags are public.                                    */
    /*----------------------------------------------------------------------*/

    public static bool APP_bDebug;                       // --debug: write the debug log
    public static bool APP_bVerbose;                     // --verbose: commands also report success
    static string APP_sCommand;                          // the APP_CMD_ to run, or null to open the editor
    static string APP_sFile;                             // file named on the command line, or null
    public static int APP_iFormat;                       // --format NAME: the VoorheesFormat.VF_ format, or VF_DETECT
    static List<string> APP_lstExtensions;               // --set-default / --unset-default: the extensions (".json" ...)
    static bool APP_bLogFailed;                          // the debug log could not be written; stop trying

    /*----------------------------------------------------------------------*/
    /* AttachConsole:                                                       */
    /*                                                                      */
    /* kernel32.dll, imported.  Connects this process to a console so       */
    /* Console output appears there.  Voorhees is built as a window         */
    /* program, which Windows starts with no console, so this is how a      */
    /* command's output reaches the prompt it was typed at.                 */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     dwProcessId : APP_ATTACH_PARENT_PROCESS for the console of the   */
    /*                   program that started this one.                     */
    /*                                                                      */
    /* Returns:                                                             */
    /*     bool : true if attached; false when the parent has no console    */
    /*            (started from Explorer, say).                             */
    /*----------------------------------------------------------------------*/
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);

    /*----------------------------------------------------------------------*/
    /* FreeConsole:                                                         */
    /*                                                                      */
    /* kernel32.dll, imported.  Disconnects from the console attached by    */
    /* AttachConsole, so the prompt is not held while the editor runs.      */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     bool : true if detached.                                         */
    /*----------------------------------------------------------------------*/
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    /*----------------------------------------------------------------------*/
    /* GetConsoleWindow:                                                    */
    /*                                                                      */
    /* kernel32.dll, imported.  The window of the attached console, so      */
    /* an Enter key can be posted to it after detaching.                    */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     IntPtr : the console window, or IntPtr.Zero when there is none.  */
    /*----------------------------------------------------------------------*/
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    /*------------------------------------------------------------------------*/
    /* PostMessage:                                                           */
    /*                                                                        */
    /* user32.dll, imported.  Queues a window message without waiting         */
    /* for it to be handled; used to press Enter in the console window.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hWnd   : the window to receive the message.                        */
    /*     Msg    : APP_WM_KEYDOWN or APP_WM_KEYUP.                           */
    /*     wParam : the virtual key, APP_VK_RETURN.                           */
    /*     lParam : key flags; zero.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true if the message was queued.                             */
    /*------------------------------------------------------------------------*/
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam,
        IntPtr lParam);

    /*------------------------------------------------------------------------*/
    /* Main:                                                                  */
    /*                                                                        */
    /* Command line first, editor second:                                     */
    /*     1. Attach to the starting console, if there is one, before         */
    /*        anything else, so command output is visible even though         */
    /*        this is a window program.                                       */
    /*     2. Read every argument (APP_ParseArgs); a bad command line is      */
    /*        reported with the usage and ends here.                          */
    /*     3. A command (--validate, --register, ...) runs and its exit       */
    /*        code is returned at once: no window, no pause.                  */
    /*     4. Otherwise the editor opens.  A file named on the command        */
    /*        line is queued to open once the window has loaded; that is      */
    /*        how Explorer's "Edit with Voorhees" and a .json double          */
    /*        click hand a file over.  A path that does not exist is          */
    /*        reported on the console and an empty document opens.  Then      */
    /*        the console is let go (and Enter pressed in it, so its          */
    /*        prompt comes back at once), visual styles are switched on       */
    /*        before any control exists, and the window is built and run.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     args : the command line, already split into words.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the command's APP_EXIT_ code, or APP_EXIT_OK when the        */
    /*           editor window has closed.                                    */
    /*------------------------------------------------------------------------*/
    [STAThread]
    static int Main(string[] args)
    {
        MainForm form;                                   // the editor window
        bool bAttached;                                  // a console is attached
        IntPtr hConsole;                                 // its window, for the Enter key
        string sError;                                   // what is wrong with the command line
        int iRc;                                         // a command's exit code

        /* 1. Console first: output from here on reaches the prompt. */
        bAttached = AttachConsole(APP_ATTACH_PARENT_PROCESS);

        /* 2. Every argument, before any window exists. */
        sError = APP_ParseArgs(args);
        if (sError != null)
        {   /* Not understood: say what and how to use it, and stop. */
            Console.Error.WriteLine("Voorhees: " + sError);
            Console.Error.WriteLine();
            APP_PrintUsage();
            return(APP_EXIT_USAGE);
        }

        /* 3. A command runs and exits without opening the editor. */
        if (APP_sCommand != null)
        {   /* Command mode: its exit code is the program's. */
            iRc = APP_RunCommand();
            return(iRc);
        }

        /* 4. The editor.  A missing file is said while the console is   */
        /* still attached; the editor then opens empty.                  */
        if (APP_sFile != null && !File.Exists(APP_sFile))
        {   /* Named but not there: nothing to open. */
            Console.Error.WriteLine("Voorhees: " + APP_sFile
                + ": file not found; opening an empty document.");
            APP_sFile = null;
        }

        if (bAttached)
        {   /* Let the console go, and press Enter in it so its prompt is redrawn at once. */
            hConsole = GetConsoleWindow();
            FreeConsole();
            if (hConsole != IntPtr.Zero)
            {   /* A real console window: post the key to it. */
                PostMessage(hConsole, APP_WM_KEYDOWN,
                    (IntPtr)APP_VK_RETURN, IntPtr.Zero);
                PostMessage(hConsole, APP_WM_KEYUP,
                    (IntPtr)APP_VK_RETURN, IntPtr.Zero);
            }
        }

        /* Visual styles and the text rendering mode must be chosen   */
        /* before the first control is created.                       */
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        /* The window is built by hand, here, after all of the above. */
        form = new MainForm();
        if (APP_sFile != null)
        {   /* A file was named: open it once the window has loaded. */
            form.CMD_OpenFileOnLoad(APP_sFile);
        }

        APP_DebugLog("editor started, version " + VoorheesVersion.V);

        /* Run the message loop until the editor window closes. */
        Application.Run(form);

        /* The editor has closed normally. */
        return(APP_EXIT_OK);
    }

    /*------------------------------------------------------------------------*/
    /* APP_ParseArgs:                                                         */
    /*                                                                        */
    /* Reads the whole command line into APP_sCommand, APP_sFile and the      */
    /* mode flags.  Options are not case sensitive.  Rules:                   */
    /*     o at most one command; --help and --version take no file;          */
    /*     o --validate needs exactly one file; --register and                */
    /*       --unregister take none;                                          */
    /*     o --debug only means something to the editor, and --verbose        */
    /*       only to a command; each is refused where it has no effect,       */
    /*       rather than silently ignored;                                    */
    /*     o --format NAME (json, ini, klipper, moonraker, klipperscreen,     */
    /*       reg, or a format's full name) reads the file as that format      */
    /*       instead of the one its name or contents show; it goes with the   */
    /*       editor (with no file, it makes a new document of the format) or  */
    /*       --validate;                                                      */
    /*     o anything starting with "-" that is not an option is an error;    */
    /*       anything else is the file, and only one file is accepted.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     args : the command line words.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null when the command line makes sense, else what is      */
    /*              wrong with it.                                            */
    /*------------------------------------------------------------------------*/
    static string APP_ParseArgs(string[] args)
    {
        string sArg;                                     // one word, as typed
        string sLower;                                   // the same, lower case, for option matching
        string sNewCommand;                              // command this word asks for, or null
        string sProblem;                                 // why an extension list is refused
        int i;

        APP_bDebug = false;
        APP_bVerbose = false;
        APP_sCommand = null;
        APP_sFile = null;
        APP_iFormat = VoorheesFormat.VF_DETECT;

        for (i = 0; i < args.Length; i++)
        {
            sArg = args[i];
            sLower = sArg.ToLowerInvariant();
            sNewCommand = null;

            if (sLower == "--help" || sLower == "-h" || sLower == "/?")
            {   /* Usage. */
                sNewCommand = APP_CMD_HELP;
            }
            else if (sLower == "--version")
            {   /* Version number. */
                sNewCommand = APP_CMD_VERSION;
            }
            else if (sLower == "--validate")
            {   /* Check a file. */
                sNewCommand = APP_CMD_VALIDATE;
            }
            else if (sLower == "--register")
            {   /* Explorer integration on. */
                sNewCommand = APP_CMD_REGISTER;
            }
            else if (sLower == "--unregister")
            {   /* Explorer integration off. */
                sNewCommand = APP_CMD_UNREGISTER;
            }
            else if (sLower == "--set-default" || sLower == "--unset-default")
            {   /* Voorhees as the default for some extensions, or not: the next word lists them. */
                sNewCommand = sLower.Substring(2);
                if (i + 1 >= args.Length)
                {   /* No list. */
                    return(sArg + " needs extensions: json, ini, cfg, conf, reg "
                        + "(comma-separated)");
                }
                i++;
                APP_lstExtensions = MainForm.REG_ParseExtensions(args[i], out sProblem);
                if (APP_lstExtensions == null)
                {   /* Not Voorhees' extensions. */
                    return(sArg + ": " + sProblem);
                }
            }
            else if (sLower == "--debug")
            {   /* Editor debug log. */
                APP_bDebug = true;
            }
            else if (sLower == "--verbose")
            {   /* Commands also report success. */
                APP_bVerbose = true;
            }
            else if (sLower == "--format")
            {   /* The format to read the file as: the next word names it. */
                if (i + 1 >= args.Length)
                {   /* No name after it. */
                    return("--format needs a format: json, ini, klipper, "
                        + "moonraker, klipperscreen or reg");
                }
                i++;
                APP_iFormat = VoorheesFormat.Vf_FromName(args[i]);
                if (APP_iFormat == VoorheesFormat.VF_DETECT)
                {   /* Not a format's name. */
                    return("unknown format \"" + args[i] + "\" (json, ini, "
                        + "klipper, moonraker, klipperscreen or reg)");
                }
            }
            else if (sArg.Length > 1 && sArg[0] == '-')
            {   /* Looks like an option but is not one. */
                return("unknown option \"" + sArg + "\"");
            }
            else if (APP_sFile == null)
            {   /* The file. */
                APP_sFile = sArg;
            }
            else
            {   /* A second file. */
                return("only one file can be given (\"" + APP_sFile
                    + "\" and \"" + sArg + "\")");
            }

            if (sNewCommand != null)
            {   /* A command word: only one is allowed. */
                if (APP_sCommand != null && APP_sCommand != sNewCommand)
                {   /* Two different commands. */
                    return("--" + APP_sCommand + " and --" + sNewCommand
                        + " cannot be used together");
                }
                APP_sCommand = sNewCommand;
            }
        }

        if (APP_sCommand == APP_CMD_VALIDATE && APP_sFile == null)
        {   /* Nothing to validate. */
            return("--validate needs a file");
        }

        if (APP_sCommand != null && APP_sCommand != APP_CMD_VALIDATE
            && APP_sFile != null)
        {   /* A file given to a command that does not read one. */
            return("--" + APP_sCommand + " does not take a file");
        }

        if (APP_sCommand != null && APP_bDebug)
        {   /* The debug log belongs to the editor. */
            return("--debug applies to the editor, not to --" + APP_sCommand);
        }

        if (APP_sCommand == null && APP_bVerbose)
        {   /* Nothing to be verbose about. */
            return("--verbose applies to --validate, --register, --unregister, "
                + "--set-default and --unset-default");
        }

        if (APP_iFormat != VoorheesFormat.VF_DETECT && APP_sCommand != null
            && APP_sCommand != APP_CMD_VALIDATE)
        {   /* A format for a command that reads no file. */
            return("--format applies to the editor and --validate, not to --"
                + APP_sCommand);
        }

        /* The command line makes sense. */
        return(null);
    }

    /*----------------------------------------------------------------------*/
    /* APP_PrintUsage:                                                      */
    /*                                                                      */
    /* Prints the command line summary and the exit codes to stderr.        */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the usage is written to stderr.                           */
    /*----------------------------------------------------------------------*/
    static void APP_PrintUsage()
    {
        Console.Error.WriteLine("Voorhees " + VoorheesVersion.V
            + " - a JSON, INI, Klipper config and .reg editor for Windows");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Voorhees [--debug] [--format NAME] [file]");
        Console.Error.WriteLine("                                           open the editor, optionally with a file");
        Console.Error.WriteLine("  Voorhees --validate file [--format NAME] [--verbose]");
        Console.Error.WriteLine("                                           check that a file is valid in its format");
        Console.Error.WriteLine("  Voorhees --register [--verbose]          add \"Edit with Voorhees\" and Open With");
        Console.Error.WriteLine("                                           for .json .ini .cfg .conf .reg");
        Console.Error.WriteLine("  Voorhees --set-default EXT[,EXT...] [--verbose]");
        Console.Error.WriteLine("                                           make Voorhees the default for them");
        Console.Error.WriteLine("  Voorhees --unset-default EXT[,EXT...] [--verbose]");
        Console.Error.WriteLine("                                           put their previous defaults back");
        Console.Error.WriteLine("  Voorhees --unregister [--verbose]        remove it all again");
        Console.Error.WriteLine("  Voorhees --version                       print the version");
        Console.Error.WriteLine("  Voorhees --help                          print this help");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --debug    write load, save and tree timings to");
        Console.Error.WriteLine("             " + APP_LOGDIR + "\\" + APP_LOGFILE + " beside Voorhees.exe");
        Console.Error.WriteLine("  --verbose  report success as well as failure");
        Console.Error.WriteLine("  --format   read the file as json, ini, klipper, moonraker,");
        Console.Error.WriteLine("             klipperscreen or reg; without it the format comes");
        Console.Error.WriteLine("             from the file's name, else its contents");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Exit codes:");
        Console.Error.WriteLine("  " + APP_EXIT_OK.ToString() + "  success");
        Console.Error.WriteLine("  " + APP_EXIT_INVALID.ToString() + "  --validate: the file is not valid in its format");
        Console.Error.WriteLine("  " + APP_EXIT_USAGE.ToString() + "  the command line could not be understood");
        Console.Error.WriteLine("  " + APP_EXIT_UNREADABLE.ToString() + "  the file could not be read");
        Console.Error.WriteLine("  " + APP_EXIT_REGFAILED.ToString() + "  the registry could not be changed");
    }

    /*----------------------------------------------------------------------*/
    /* APP_RunCommand:                                                      */
    /*                                                                      */
    /* Runs the command APP_ParseArgs found.  No window is created.         */
    /*     o help       : the usage, on stderr.                             */
    /*     o version    : "Voorhees x.y.z" on stdout (the one thing asked   */
    /*                    for, so it is printed, not just its success).     */
    /*     o validate   : reads and parses the file as the editor would     */
    /*                    (MainForm.FILE_ReadDocument): strict JSON, with   */
    /*                    // and block comments allowed.  A failure is      */
    /*                    reported on stderr with its line and column.      */
    /*                    Repeated keys (valid JSON, but lost by many       */
    /*                    readers) and comments (which make the file JSONC, */
    /*                    refused by standard readers) earn a warning;      */
    /*                    --verbose adds a summary line.                    */
    /*     o register / unregister : the Explorer integration, as the       */
    /*                    Tools menu does it (MainForm.REG_DoRegister /     */
    /*                    REG_DoUnregister).                                */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.  Reads APP_sCommand, APP_sFile and APP_bVerbose.           */
    /*                                                                      */
    /* Returns:                                                             */
    /*     int : the APP_EXIT_ code for the outcome.                        */
    /*----------------------------------------------------------------------*/
    static int APP_RunCommand()
    {
        VoorheesFileData data;                           // the file as read and parsed
        List<string> lstWarnings;                        // --validate's warnings about a valid file
        string sError;                                   // why a registry change failed
        int i;

        if (APP_sCommand == APP_CMD_HELP)
        {   /* Usage was asked for: that is success. */
            APP_PrintUsage();
            return(APP_EXIT_OK);
        }

        if (APP_sCommand == APP_CMD_VERSION)
        {   /* The version is the output itself. */
            Console.WriteLine("Voorhees " + VoorheesVersion.V);
            return(APP_EXIT_OK);
        }

        if (APP_sCommand == APP_CMD_VALIDATE)
        {   /* Read and parse, exactly as File > Open would. */
            data = MainForm.FILE_ReadDocument(APP_sFile, APP_iFormat);

            if (data.Vfd_bReadFailed)
            {   /* The file itself could not be read. */
                Console.Error.WriteLine("Voorhees: " + APP_sFile + ": "
                    + data.Vfd_sError);
                return(APP_EXIT_UNREADABLE);
            }

            if (data.Vfd_sError != null)
            {   /* Read, but not valid in its format. */
                Console.Error.WriteLine("Voorhees: " + APP_sFile + ": "
                    + data.Vfd_sError);
                return(APP_EXIT_INVALID);
            }

            /* Valid, but worth a second look: the format's warnings (JSON   */
            /* repeated keys and comments; INI repeats and keys alone).      */
            lstWarnings = VoorheesFormat.Vf_ValidateWarnings(data.Vfd_doc,
                data.Vfd_iExtraCopies, data.Vfd_bHasComments);
            for (i = 0; i < lstWarnings.Count; i++)
            {
                Console.Error.WriteLine("Voorhees: " + APP_sFile
                    + ": warning: " + lstWarnings[i]);
            }

            if (APP_bVerbose)
            {   /* Asked to report success too, naming the format read. */
                Console.WriteLine(APP_sFile + ": valid "
                    + VoorheesFormat.Vf_Name(data.Vfd_doc.Vd_iFormat) + " ("
                    + MainForm.FILE_DescribeFormat(data) + ")");
            }
            return(APP_EXIT_OK);
        }

        /* Register, unregister, set or unset defaults: the same work as   */
        /* the Tools menu.                                                 */
        sError = null;
        if (APP_sCommand == APP_CMD_REGISTER)
        {   /* Add the Explorer integration. */
            sError = MainForm.REG_DoRegister();
        }
        else if (APP_sCommand == APP_CMD_UNREGISTER)
        {   /* Remove it. */
            sError = MainForm.REG_DoUnregister();
        }
        else
        {   /* Each extension's default, stopping at the first refusal. */
            foreach (string sExt in APP_lstExtensions)
            {
                if (sError != null)
                {   /* An earlier one failed. */
                    break;
                }
                if (APP_sCommand == APP_CMD_SETDEFAULT)
                {   /* Voorhees becomes the default. */
                    sError = MainForm.REG_DoSetDefault(sExt);
                }
                else
                {   /* The previous default comes back. */
                    sError = MainForm.REG_DoUnsetDefault(sExt);
                }
            }
        }

        if (sError != null)
        {   /* The registry refused. */
            Console.Error.WriteLine("Voorhees: --" + APP_sCommand
                + " failed: " + sError);
            return(APP_EXIT_REGFAILED);
        }

        if (APP_bVerbose)
        {   /* Asked to report success too. */
            Console.WriteLine("Voorhees: --" + APP_sCommand + " done.");
        }

        /* The registry was changed. */
        return(APP_EXIT_OK);
    }

    /*----------------------------------------------------------------------*/
    /* APP_DebugLog:                                                        */
    /*                                                                      */
    /* With --debug, appends one time-stamped line to log\                  */
    /* Voorhees_debug.log beside the exe, creating the folder if needed.    */
    /* Without --debug it does nothing.  It is a diagnostic aid, so a log   */
    /* that cannot be written must not get in the editor's way: the first   */
    /* failure turns logging off for the rest of the run, silently.         */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sMessage : the line to write.                                    */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the line is appended, or nothing happens.                 */
    /*----------------------------------------------------------------------*/
    public static void APP_DebugLog(string sMessage)
    {
        string sDir;                                     // log folder beside the exe

        if (!APP_bDebug || APP_bLogFailed)
        {   /* Not asked for, or not writable: nothing to do. */
            return;
        }

        try
        {
            sDir = Path.Combine(Path.GetDirectoryName(
                Application.ExecutablePath), APP_LOGDIR);
            Directory.CreateDirectory(sDir);

            /* Time to the millisecond, so timings line up with events. */
            File.AppendAllText(Path.Combine(sDir, APP_LOGFILE),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture)
                + "  " + sMessage + Environment.NewLine);
        }
        catch
        {   /* Unwritable log: stop trying for the rest of this run. */
            APP_bLogFailed = true;
        }
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesFileData: a file as read, before it is the document                */
/*----------------------------------------------------------------------------*/

/*--------------------------------------------------------------------------*/
/* VoorheesFileData                                                         */
/*                                                                          */
/* What MainForm.FILE_ReadDocument learned about a file: the document (with */
/* its format and style) and what the user should be told about it, or why  */
/* it could not be read.  It holds no controls, so it can be filled on a    */
/* worker thread (behind the progress dialog) and used afterwards on the    */
/* UI thread, and the command line's --validate uses the same reading.      */
/*--------------------------------------------------------------------------*/
class VoorheesFileData
{
    public VoorheesDocument Vfd_doc;                     // the document, or null on failure
    public int Vfd_iExtraCopies;                         // repeated-key copies in the document
    public bool Vfd_bHasComments;                        // the file holds comments (JSONC)
    public long Vfd_lBytes;                              // file size, bytes
    public bool Vfd_bReadFailed;                         // the file could not be read at all
    public string Vfd_sError;                            // why reading or parsing failed, or null
}

/*----------------------------------------------------------------------------*/
/* MainForm: the editor window                                                */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* MainForm                                                                   */
/*                                                                            */
/* The editor window and everything it does.  Functions are grouped by        */
/* the prefix of their names:                                                 */
/*     INIT_   : builds the menus, tree, edit panel and status strip.         */
/*     EDIT_   : the edit panel, its Save and Discard, undo and redo.         */
/*     VIEW_   : the font size and the raw-text view option.                  */
/*     CTX_    : the tree's right-click menu.                                 */
/*     FILE_   : new, open, close, save, save as.                             */
/*     DOC_    : finishing an edit: the tree, the title, the comment          */
/*               warning.                                                     */
/*     PROG_   : the progress dialog for long work.                           */
/*     STATE_  : title bar and status strip.                                  */
/*     TREE_   : the tree's selection and in-place editing.                   */
/*     SEARCH_ : find and replace.                                            */
/*     DLG_    : the small text input dialog.                                 */
/*     CMD_    : command line hand-over from Main.                            */
/*     CFG_    : settings kept in the registry.                               */
/*     REG_    : Explorer integration for .json files.                        */
/*     HELP_   : the About box.                                               */
/*     DND_    : opening a file dragged onto the window.                      */
/*                                                                            */
/* The class is partial for one reason: the test harness                      */
/* (tools\VoorheesHarness.cs, built into its own exe, never into              */
/* Voorhees.exe) adds its half of the class to drive the editor from a        */
/* script.  The few places where the editor would stop for the user -- a      */
/* message box, the input box, a file picker -- and the registry settings     */
/* call partial methods (DLG_Hook..., CFG_HookSkip) that only the harness     */
/* implements.  In Voorhees.exe they have no body, and the compiler           */
/* removes the calls entirely.                                                */
/*----------------------------------------------------------------------------*/
partial class MainForm : Form
{
    /*------------------------------------------------------------------------*/
    /* Controls                                                               */
    /*------------------------------------------------------------------------*/

    MenuStrip menuStrip;                                 // the menu bar
    ToolStripMenuItem fileMenu;                          // File menu
    ToolStripMenuItem FILE_tsmReopenAs;                  // File > Reopen As, greyed while no file is open
    ToolStripMenuItem editMenu;                          // Edit menu
    ToolStripMenuItem searchMenu;                        // Search menu
    ToolStripMenuItem toolsMenu;                         // Tools menu
    ToolStripMenuItem helpMenu;                          // Help menu
    ToolStripMenuItem viewMenu;                          // View menu
    ToolStripMenuItem VIEW_tsmShowRaw;                   // View > Show Raw Text in Tree, ticked while on
    ToolStripMenuItem VIEW_tsmColorKeys;                 // View > Color Keys, ticked while on
    List<ToolStripMenuItem> fontSizeItems;               // View > Font Size preset items, checked = current
    CheckBox chkBoolValue;                               // value editor shown instead of txtValue for Boolean
    Button btnDiscard;                                   // edit panel Discard: reload the panel from the document
    bool editPanelDirty;                                 // the panel holds typed changes not yet saved to the document
    bool EDIT_bRawDirty;                                 // those changes are in the Raw box (the other fields are locked)
    bool suppressPanelDirty;                             // set while the panel is loaded from code, so it is not marked dirty
    SplitContainer splitContainer;                       // tree on the left, edit panel on the right
    VoorheesTreeView tree;                               // the document as a tree
    Panel editPanel;                                     // right-hand panel holding the editors below
    Label lblKey;                                        // "Key:" caption
    TextBox txtKey;                                      // member key; read-only for array items, comments and the top value
    Label lblValue;                                      // "Value:" caption
    TextBox txtValue;                                    // value of a string, number or null; a comment entry's words
    Label lblType;                                       // "Type:" caption
    ComboBox cboType;                                    // the value kinds of EDIT_lstTypeKinds, then Comment
    List<int> EDIT_lstTypeKinds;                         // the kind behind each Type box entry but the last (Comment)
    Label EDIT_lblComment;                               // "Comment:" caption
    TextBox EDIT_txtComment;                             // the comment on the selected entry's line
    Label EDIT_lblRaw;                                   // "Raw:" caption
    TextBox EDIT_txtRaw;                                 // the selected entry's text exactly as in the file
    Font EDIT_fontRaw;                                   // fixed-pitch font of the Raw box
    Button btnApply;                                     // edit panel Save: write the panel into the document
    StatusStrip statusStrip;                             // status bar
    ToolStripStatusLabel lblFilePath;                    // full path of the open file
    ToolStripStatusLabel STATE_lblFormat;                // the document's format ("JSON", "INI" ...)
    ToolStripStatusLabel STATE_lblComments;              // "Contains comments" while the document has any
    ToolStripStatusLabel lblModified;                    // "Modified" while there are unsaved changes
    ContextMenuStrip treeContextMenu;                    // right-click menu, rebuilt on every opening
    bool CTX_bEmptySpace;                                // the last right click was on empty space (read once by CTX_Opening)

    /*------------------------------------------------------------------------*/
    /* Document and editor state                                              */
    /*------------------------------------------------------------------------*/

    VoorheesDocument doc;                                // the document being edited
    bool DOC_bHasComments;                               // the document holds a comment now
    bool DOC_bEditWarned;                                // the format's edit warning (JSON: now JSONC) was shown for this document
    string EDIT_sCommentShown;                           // the Comment box's text as loaded, to see whether it was edited
    string EDIT_sRawSource;                              // the Raw box's text as rendered (before CRLF for the box)
    string EDIT_sRawShown;                               // the Raw box's text as loaded, to see whether it was edited
    bool EDIT_bRawTruncated;                             // the Raw box shows only the start of a huge entry (read-only)
    bool labelEditing;                                   // an in-tree value edit box is open
    bool inlineEditRequested;                            // the next label edit was started by TREE_BeginInlineEdit
    VoorheesItem clipboardItem;                          // entry copied with Copy (a private deep copy), for Paste
    int CTX_iClipboardFormat;                            // the VoorheesFormat.VF_ format clipboardItem was copied from (Paste converts from it)
    FindReplaceForm findForm;                            // the Find / Replace window while it is open
    string pendingLoadPath;                              // file from the command line, opened in OnLoad
    double splitRatio;                                   // splitter position as a fraction of the width
    float fontSize;                                      // tree and panel font size, points
    Font viewFont;                                       // the font currently given to the tree and panel

    /*------------------------------------------------------------------------*/
    /* View font sizes, points.  Larger / Smaller step through the            */
    /* presets; the default is the standard WinForms control font size,       */
    /* which is not itself a preset.                                          */
    /*------------------------------------------------------------------------*/

    static readonly float[] VIEW_FONTSIZES = new float[]
    {
        8f, 9f, 10f, 11f, 12f, 14f, 16f, 18f, 20f, 24f   // View > Font Size preset menu items
    };
    const float VIEW_FONTMIN = 6f;                       // smallest size accepted from the registry or code
    const float VIEW_FONTMAX = 36f;                      // largest size accepted from the registry or code

    /*------------------------------------------------------------------------*/
    /* The Type box's entries are the value kinds the format offers for the   */
    /* selected entry (VoorheesFormat.Vf_PanelKinds, kept in                  */
    /* EDIT_lstTypeKinds), then Comment, shown (and fixed) for a comment      */
    /* entry; EDIT_KINDCOMMENT stands for that last entry wherever a kind is  */
    /* expected.  And the Raw box's size limit: an entry whose text is        */
    /* longer is shown cut, read-only.                                        */
    /*------------------------------------------------------------------------*/

    const int EDIT_KINDCOMMENT = -1;                     // "kind" of the Type box's Comment entry, and of Add Child > Comment
    const int EDIT_RAWMAX = 64 * 1024;                   // longest entry text editable in the Raw box, characters

    /*------------------------------------------------------------------------*/
    /* MainForm:                                                              */
    /*                                                                        */
    /* Builds the editor window.  The controls are created by the INIT_       */
    /* functions, then re-added to the form in the order docking needs,       */
    /* every control a file can be dropped on is told to accept drops,        */
    /* and an empty {} document is opened.  Saved window settings are         */
    /* applied later, in OnLoad, once the form has a real size.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     MainForm : the constructed, not yet shown, window.                 */
    /*------------------------------------------------------------------------*/
    public MainForm()
    {
        Stream icoStream;                                // embedded icon resource

        /* State that does not depend on the document.  The document   */
        /* itself is created by FILE_New at the end.                   */
        splitRatio = 0.75;
        fontSize = Control.DefaultFont.SizeInPoints;
        fontSizeItems = new List<ToolStripMenuItem>();

        /* Window basics.  A saved size and position replace these in   */
        /* OnLoad (CFG_Load); the title names the document as soon as   */
        /* one is shown (STATE_UpdateTitle).                            */
        Text = "Voorhees";
        Size = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        /* Title bar and taskbar icon, from the copy of Voorhees.ico   */
        /* compiled into the exe by compile.bat's /resource option.    */
        try
        {
            icoStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Voorhees.ico");
            if (icoStream != null)
            {   /* Resource present: use it, then release the stream. */
                Icon = new Icon(icoStream);
                icoStream.Close();
            }
        }
        catch
        {   /* Unreadable icon resource: keep the default Windows icon. */
        }

        /* Create every control. */
        INIT_MenuStrip();
        INIT_SplitContainer();
        INIT_EditPanel();
        INIT_StatusStrip();
        INIT_ContextMenu();

        /* Docking order: WinForms docks controls in reverse order of    */
        /* addition, so the Fill control must be added FIRST to end up   */
        /* filling only the space the menu (top) and status strip        */
        /* (bottom) leave.  The INIT_ functions added them in creation   */
        /* order, so clear and re-add them in the right one.             */
        Controls.Clear();
        Controls.Add(splitContainer);                    // Fill: docked last, takes the remaining space
        Controls.Add(statusStrip);                       // Bottom
        Controls.Add(menuStrip);                         // Top
        MainMenuStrip = menuStrip;

        /* Drag and drop: the tree and edit panel cover the whole          */
        /* client area, so a dropped file arrives at whichever control     */
        /* is under the mouse, never at the form.  Every one of them has   */
        /* to accept the drop.                                             */
        DND_Enable(this);
        DND_Enable(splitContainer);
        DND_Enable(tree);
        DND_Enable(editPanel);
        foreach (Control c in editPanel.Controls)
        {
            DND_Enable(c);
        }

        /* Start with an empty {} document. */
        FILE_New(VoorheesFormat.VF_JSON);
    }

    /*------------------------------------------------------------------------*/
    /* INIT: building the window's controls                                   */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* INIT_MenuStrip:                                                        */
    /*                                                                        */
    /* Builds the menu bar: File, Edit, Search, View, Tools and Help,         */
    /* with their keyboard shortcuts.  Shortcuts are processed by the         */
    /* menu before the focused control sees the key, which is why             */
    /* EDIT_Undo and EDIT_Redo check for a focused text box themselves.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : menuStrip and the menu fields are created and filled,       */
    /*            and the menu bar is added to the form.                      */
    /*------------------------------------------------------------------------*/
    void INIT_MenuStrip()
    {
        ToolStripMenuItem fontMenu;                      // View > Font Size submenu
        ToolStripMenuItem largerItem;                    // Font Size > Larger
        ToolStripMenuItem smallerItem;                   // Font Size > Smaller
        ToolStripMenuItem resetItem;                     // Font Size > Default
        ToolStripMenuItem sizeItem;                      // one preset size item
        ToolStripMenuItem item;                          // a File menu item with a shortcut

        menuStrip = new MenuStrip();

        /* File menu.  New and Close leave an empty document of the       */
        /* current format; New As picks another format; Reopen As reads   */
        /* the open file again as another format.  The separators group   */
        /* making and opening, saving, and leaving.  Each item gets its   */
        /* shortcut directly, so adding items never shifts one.           */
        fileMenu = new ToolStripMenuItem("File");
        item = new ToolStripMenuItem("New", null,
            delegate { FILE_New(doc.Vd_iFormat); });
        item.ShortcutKeys = Keys.Control | Keys.N;
        fileMenu.DropDownItems.Add(item);
        fileMenu.DropDownItems.Add(FILE_FormatSubmenu("New As", false));
        item = new ToolStripMenuItem("Open...", null, delegate { FILE_Open(); });
        item.ShortcutKeys = Keys.Control | Keys.O;
        fileMenu.DropDownItems.Add(item);
        FILE_tsmReopenAs = FILE_FormatSubmenu("Reopen As", true);
        fileMenu.DropDownItems.Add(FILE_tsmReopenAs);
        item = new ToolStripMenuItem("Close", null, delegate { FILE_Close(); });
        item.ShortcutKeys = Keys.Control | Keys.W;
        fileMenu.DropDownItems.Add(item);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        item = new ToolStripMenuItem("Save", null, delegate { FILE_Save(); });
        item.ShortcutKeys = Keys.Control | Keys.S;
        fileMenu.DropDownItems.Add(item);
        item = new ToolStripMenuItem("Save As...", null,
            delegate { FILE_SaveAs(); });
        item.ShortcutKeys = Keys.Control | Keys.Shift | Keys.S;
        fileMenu.DropDownItems.Add(item);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("Exit", null, delegate { Close(); });

        /* Reopen As needs a file: it is greyed while there is none. */
        fileMenu.DropDownOpening += delegate
        {
            FILE_tsmReopenAs.Enabled = (doc != null && doc.Vd_sPath != null);
        };

        /* Edit menu: undo and redo of document changes. */
        editMenu = new ToolStripMenuItem("Edit");
        editMenu.DropDownItems.Add("Undo", null, delegate { EDIT_Undo(); });
        editMenu.DropDownItems.Add("Redo", null, delegate { EDIT_Redo(); });

        ((ToolStripMenuItem)editMenu.DropDownItems[0]).ShortcutKeys =
            Keys.Control | Keys.Z;
        ((ToolStripMenuItem)editMenu.DropDownItems[1]).ShortcutKeys =
            Keys.Control | Keys.Y;

        /* Search menu: both items open the same window, in Find-only   */
        /* or Find + Replace layout.                                    */
        searchMenu = new ToolStripMenuItem("Search");
        searchMenu.DropDownItems.Add("Find...", null,
            delegate { SEARCH_ShowFind(false); });
        searchMenu.DropDownItems.Add("Find + Replace...", null,
            delegate { SEARCH_ShowFind(true); });

        ((ToolStripMenuItem)searchMenu.DropDownItems[0]).ShortcutKeys =
            Keys.Control | Keys.F;
        ((ToolStripMenuItem)searchMenu.DropDownItems[1]).ShortcutKeys =
            Keys.Control | Keys.H;

        /* View menu: font size of the tree and the edit panel.  Keys   */
        /* .Oemplus is the "=" / "+" key, so Ctrl++ is shown as the     */
        /* caption rather than the enum's own name.                     */
        viewMenu = new ToolStripMenuItem("View");
        fontMenu = new ToolStripMenuItem("Font Size");

        /* Larger: next preset up. */
        largerItem = new ToolStripMenuItem("Larger",
            null, delegate { VIEW_StepFontSize(1); });
        largerItem.ShortcutKeys = Keys.Control | Keys.Oemplus;
        largerItem.ShortcutKeyDisplayString = "Ctrl++";
        fontMenu.DropDownItems.Add(largerItem);

        /* Smaller: next preset down. */
        smallerItem = new ToolStripMenuItem("Smaller",
            null, delegate { VIEW_StepFontSize(-1); });
        smallerItem.ShortcutKeys = Keys.Control | Keys.OemMinus;
        smallerItem.ShortcutKeyDisplayString = "Ctrl+-";
        fontMenu.DropDownItems.Add(smallerItem);

        /* Default: back to the standard WinForms control font size. */
        resetItem = new ToolStripMenuItem("Default",
            null, delegate
            {
                VIEW_ApplyFontSize(Control.DefaultFont.SizeInPoints);
            });
        resetItem.ShortcutKeys = Keys.Control | Keys.D0;
        fontMenu.DropDownItems.Add(resetItem);

        fontMenu.DropDownItems.Add(new ToolStripSeparator());

        /* One item per preset size.  Each keeps its size in Tag, and     */
        /* VIEW_ApplyFontSize checks the item matching the current size   */
        /* through fontSizeItems.                                         */
        foreach (float presetSize in VIEW_FONTSIZES)
        {
            sizeItem = new ToolStripMenuItem(
                presetSize.ToString(CultureInfo.InvariantCulture) + " pt");
            sizeItem.Tag = presetSize;
            sizeItem.Click += delegate(object sender, EventArgs e)
            {
                VIEW_ApplyFontSize(
                    (float)((ToolStripMenuItem)sender).Tag);
            };
            fontSizeItems.Add(sizeItem);
            fontMenu.DropDownItems.Add(sizeItem);
        }

        viewMenu.DropDownItems.Add(fontMenu);

        /* Show Raw Text in Tree: a tick item, saved with the settings. */
        VIEW_tsmShowRaw = new ToolStripMenuItem("Show Raw Text in Tree",
            null, delegate { VIEW_ToggleShowRaw(); });
        viewMenu.DropDownItems.Add(VIEW_tsmShowRaw);

        /* Color Keys: names and values in their roles' colours; a tick   */
        /* item, saved too.  Colors... chooses those colours.             */
        VIEW_tsmColorKeys = new ToolStripMenuItem("Color Keys",
            null, delegate { VIEW_ToggleColorKeys(); });
        viewMenu.DropDownItems.Add(VIEW_tsmColorKeys);
        viewMenu.DropDownItems.Add("Colors...", null,
            delegate { VIEW_ShowColors(); });

        /* Tools menu: Explorer integration for Voorhees' file types. */
        toolsMenu = new ToolStripMenuItem("Tools");
        toolsMenu.DropDownItems.Add(
            "Register Explorer Context Menu", null,
            delegate { REG_RegisterContextMenu(); });
        toolsMenu.DropDownItems.Add(
            "Set Voorhees as Default...", null,
            delegate { REG_ShowDefaults(); });
        toolsMenu.DropDownItems.Add(
            "Unregister Explorer Context Menu", null,
            delegate { REG_UnregisterContextMenu(); });

        /* Help menu. */
        helpMenu = new ToolStripMenuItem("Help");
        helpMenu.DropDownItems.Add("About Voorhees...", null,
            delegate { HELP_ShowAbout(); });

        /* Menu bar order, left to right. */
        menuStrip.Items.Add(fileMenu);
        menuStrip.Items.Add(editMenu);
        menuStrip.Items.Add(searchMenu);
        menuStrip.Items.Add(viewMenu);
        menuStrip.Items.Add(toolsMenu);
        menuStrip.Items.Add(helpMenu);
        Controls.Add(menuStrip);
        MainMenuStrip = menuStrip;
    }

    /*------------------------------------------------------------------------*/
    /* INIT_SplitContainer:                                                   */
    /*                                                                        */
    /* Builds the splitter with the tree on its left and hooks up every       */
    /* tree event the editor uses.  The splitter position is kept as a        */
    /* RATIO (splitRatio) rather than a pixel count: dragging the bar         */
    /* records the new ratio, and resizing the window re-applies it, so       */
    /* the two panes keep their proportions.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : splitContainer and tree are created and wired up.           */
    /*------------------------------------------------------------------------*/
    void INIT_SplitContainer()
    {
        splitContainer = new SplitContainer();
        splitContainer.Dock = DockStyle.Fill;
        splitContainer.FixedPanel = FixedPanel.None;

        /* The user dragged the bar: remember where, as a fraction. */
        splitContainer.SplitterMoved += delegate
        {
            if (splitContainer.Width > 0)
            {   /* Real width: the ratio is meaningful, keep it. */
                splitRatio = (double)splitContainer.SplitterDistance
                    / splitContainer.Width;
            }
        };

        /* The tree.  LabelEdit is on so values can be edited in place,   */
        /* but TREE_BeforeLabelEdit lets through only the edits that      */
        /* TREE_BeginInlineEdit starts.                                   */
        tree = VoorheesTreeView.Vtv_Create();
        tree.Dock = DockStyle.Fill;
        tree.HideSelection = false;
        tree.LabelEdit = true;

        /* Selection, in-place editing, keyboard and right-click       */
        /* handlers; each TREE_ function's header says what it does.   */
        tree.BeforeSelect += TREE_BeforeSelect;
        tree.AfterSelect += TREE_AfterSelect;
        tree.BeforeLabelEdit += TREE_BeforeLabelEdit;
        tree.AfterLabelEdit += TREE_AfterLabelEdit;
        tree.NodeMouseDoubleClick += TREE_NodeMouseDoubleClick;
        tree.KeyDown += TREE_KeyDown;
        tree.MouseDown += TREE_MouseDown;

        splitContainer.Panel1.Controls.Add(tree);
        Controls.Add(splitContainer);

        /* The window was resized: put the bar back at the same ratio. */
        Resize += delegate
        {
            int newDist;                                 // splitter position for the new width, pixels

            if (splitContainer.Width > 0 && WindowState != FormWindowState.Minimized)
            {   /* Real size (a minimized window reports nonsense widths): re-apply the ratio. */
                newDist = (int)(splitContainer.Width * splitRatio);

                if (newDist >= splitContainer.Panel1MinSize
                    && newDist <= splitContainer.Width - splitContainer.Panel2MinSize)
                {   /* Within the panes' minimum sizes: SplitterDistance would throw otherwise. */
                    splitContainer.SplitterDistance = newDist;
                }
            }
        };
    }

    /*------------------------------------------------------------------------*/
    /* INIT_EditPanel:                                                        */
    /*                                                                        */
    /* Builds the right-hand edit panel: Key, Type and Value, the Boolean     */
    /* checkbox that stands in for the Value box, the Comment box (the        */
    /* comment on the entry's line), the Raw box (the entry's text exactly    */
    /* as in the file), and the Save and Discard buttons.  No positions       */
    /* are set here: EDIT_LayoutPanel places everything from the current      */
    /* font and panel size.                                                   */
    /*                                                                        */
    /* Every editor marks the panel dirty when the USER changes it.           */
    /* suppressPanelDirty is raised while EDIT_LoadPanel fills the panel      */
    /* from code, so loading an entry never looks like an edit.  Typing in    */
    /* the Raw box locks the other fields, and typing in them locks the       */
    /* Raw box, until Save or Discard (EDIT_MarkDirty).                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : editPanel and its controls are created and wired up.        */
    /*------------------------------------------------------------------------*/
    void INIT_EditPanel()
    {
        editPanel = new Panel();
        editPanel.Dock = DockStyle.Fill;

        /* Key row. */
        lblKey = new Label();
        lblKey.Text = "Key:";
        lblKey.AutoSize = true;
        editPanel.Controls.Add(lblKey);

        txtKey = new TextBox();
        txtKey.TextChanged += delegate { EDIT_MarkDirty(false); };
        editPanel.Controls.Add(txtKey);

        /* Type row.  Changing the type also swaps the value editor   */
        /* (text box or checkbox) through EDIT_UpdateValueEditor.     */
        lblType = new Label();
        lblType.Text = "Type:";
        lblType.AutoSize = true;
        editPanel.Controls.Add(lblType);

        /* A list to choose from (no typing): the format's value      */
        /* kinds for the selected entry, filled by EDIT_FillTypeBox   */
        /* when an entry is loaded, then Comment for comment          */
        /* entries.  It starts with Comment alone.                    */
        cboType = new ComboBox();
        cboType.DropDownStyle = ComboBoxStyle.DropDownList;
        EDIT_lstTypeKinds = new List<int>();
        cboType.Items.Add("Comment");
        cboType.SelectedIndexChanged += delegate
        {
            EDIT_UpdateValueEditor();
            EDIT_MarkDirty(false);
        };
        editPanel.Controls.Add(cboType);

        /* Value row: a multi-line box for strings, numbers and null. */
        lblValue = new Label();
        lblValue.Text = "Value:";
        lblValue.AutoSize = true;
        editPanel.Controls.Add(lblValue);

        txtValue = new TextBox();
        txtValue.Multiline = true;
        txtValue.ScrollBars = ScrollBars.Vertical;
        txtValue.TextChanged += delegate { EDIT_MarkDirty(false); };
        editPanel.Controls.Add(txtValue);

        /* Boolean editor.  It sits where the Value box is and is shown   */
        /* only while the type is Boolean.  Its caption follows its       */
        /* state, so it reads "true" or "false".                          */
        chkBoolValue = new CheckBox();
        chkBoolValue.Text = "true";
        chkBoolValue.AutoSize = true;
        chkBoolValue.Visible = false;
        chkBoolValue.CheckedChanged += delegate
        {
            if (chkBoolValue.Checked)
            {   /* Ticked: the value is true. */
                chkBoolValue.Text = "true";
            }
            else
            {   /* Cleared: the value is false. */
                chkBoolValue.Text = "false";
            }
            EDIT_MarkDirty(false);
        };
        editPanel.Controls.Add(chkBoolValue);

        /* Comment row: the comment on the entry's line; empty = none. */
        EDIT_lblComment = new Label();
        EDIT_lblComment.Text = "Comment:";
        EDIT_lblComment.AutoSize = true;
        editPanel.Controls.Add(EDIT_lblComment);

        EDIT_txtComment = new TextBox();
        EDIT_txtComment.TextChanged += delegate { EDIT_MarkDirty(false); };
        editPanel.Controls.Add(EDIT_txtComment);

        /* Raw row: the entry's text exactly as in the file, editable;   */
        /* no wrapping, so lines read as they are in the file.           */
        EDIT_lblRaw = new Label();
        EDIT_lblRaw.Text = "Raw:";
        EDIT_lblRaw.AutoSize = true;
        editPanel.Controls.Add(EDIT_lblRaw);

        EDIT_txtRaw = new TextBox();
        EDIT_txtRaw.Multiline = true;
        EDIT_txtRaw.WordWrap = false;
        EDIT_txtRaw.ScrollBars = ScrollBars.Both;
        EDIT_txtRaw.TextChanged += delegate { EDIT_MarkDirty(true); };
        editPanel.Controls.Add(EDIT_txtRaw);

        /* Save: write the panel into the document. */
        btnApply = new Button();
        btnApply.Text = "Save";
        btnApply.Click += EDIT_ApplyClick;
        editPanel.Controls.Add(btnApply);

        /* Discard: throw away typed changes and reload the panel. */
        btnDiscard = new Button();
        btnDiscard.Text = "Discard";
        btnDiscard.Click += delegate { EDIT_DiscardClick(); };
        editPanel.Controls.Add(btnDiscard);

        splitContainer.Panel2.Controls.Add(editPanel);

        /* Lay the panel out again whenever its size changes. */
        editPanel.Resize += delegate { EDIT_LayoutPanel(); };
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_LayoutPanel:                                                      */
    /*                                                                        */
    /* Places every edit panel control from the current font and the          */
    /* panel's size; nothing in the panel has a fixed position.               */
    /*     o The caption column is as wide as the widest caption.             */
    /*     o Rows are spaced by the controls' real heights.                   */
    /*     o The Type box is just wide enough for its longest entry.          */
    /*     o Save and Discard sit bottom right, sized to the wider            */
    /*       caption.                                                         */
    /*     o The height between the Type row and the buttons, less the        */
    /*       Comment row, is shared by the Value box (two fifths) and the     */
    /*       Raw box (the rest).                                              */
    /* Called on every panel resize and after every font size change.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel's controls are moved and resized.                 */
    /*------------------------------------------------------------------------*/
    void EDIT_LayoutPanel()
    {
        int margin;                                      // space between the panel edge and the controls
        int gap;                                         // space between rows and between caption and field
        int rowHeight;                                   // height of a single-line text box at this font
        int labelWidth;                                  // width of the caption column
        int fieldLeft;                                   // x of the editors, right of the captions
        int fieldWidth;                                  // width of the Key, Value, Comment and Raw boxes
        int comboWidth;                                  // width of the Type box
        int btnWidth;                                    // width of each button
        int btnHeight;                                   // height of each button
        int btnY;                                        // y of the button row
        int freeHeight;                                  // height shared by the Value and Raw boxes
        int valueHeight;                                 // height of the Value box
        int rawHeight;                                   // height of the Raw box
        int y;                                           // top of the row being placed

        if (editPanel == null || btnDiscard == null)
        {   /* Called by a resize during construction, before every control exists. */
            return;
        }

        margin = 8;
        gap = 6;

        /* A single-line text box's height follows its font; the   */
        /* captions and the checkbox are centred against it.       */
        rowHeight = txtKey.Height;

        /* Caption column and the editors to its right.  The editors   */
        /* never shrink below 60 pixels, however narrow the panel.     */
        labelWidth = Math.Max(Math.Max(lblKey.PreferredWidth, lblType.PreferredWidth),
            Math.Max(Math.Max(lblValue.PreferredWidth, EDIT_lblComment.PreferredWidth),
            EDIT_lblRaw.PreferredWidth));
        fieldLeft = margin + labelWidth + gap;
        fieldWidth = editPanel.ClientSize.Width - fieldLeft - margin - 4;
        if (fieldWidth < 60)
        {   /* Panel narrower than the captions allow: keep the editors usable. */
            fieldWidth = 60;
        }

        /* Key row. */
        y = margin + 4;
        txtKey.SetBounds(fieldLeft, y, fieldWidth, rowHeight);
        lblKey.Location = new Point(margin,
            y + (rowHeight - lblKey.Height) / 2);
        y += rowHeight + gap;

        /* Type row.  The box is measured against its longest entry   */
        /* plus room for the drop-down arrow, never wider than the    */
        /* other editors.                                             */
        comboWidth = 0;
        foreach (object item in cboType.Items)
        {
            comboWidth = Math.Max(comboWidth, TextRenderer.MeasureText(
                item.ToString(), cboType.Font).Width);
        }
        comboWidth += SystemInformation.VerticalScrollBarWidth + 12;
        if (comboWidth > fieldWidth)
        {   /* Narrow panel: the combo shrinks with the other editors. */
            comboWidth = fieldWidth;
        }
        cboType.SetBounds(fieldLeft, y, comboWidth, cboType.Height);
        lblType.Location = new Point(margin,
            y + (cboType.Height - lblType.Height) / 2);
        y += cboType.Height + gap;

        /* Save and Discard: both as wide as the wider caption plus      */
        /* padding, never smaller than a standard 85 x 28 button, side   */
        /* by side at the bottom right with Save outermost.              */
        btnWidth = Math.Max(
            TextRenderer.MeasureText(btnApply.Text, btnApply.Font).Width,
            TextRenderer.MeasureText(btnDiscard.Text, btnDiscard.Font).Width)
            + 24;
        if (btnWidth < 85)
        {   /* Small font: keep the standard button width. */
            btnWidth = 85;
        }

        btnHeight = btnApply.Font.Height + 12;
        if (btnHeight < 28)
        {   /* Small font: keep the standard button height. */
            btnHeight = 28;
        }

        btnY = editPanel.ClientSize.Height - btnHeight - margin;
        btnApply.SetBounds(editPanel.ClientSize.Width - btnWidth - margin - 4,
            btnY, btnWidth, btnHeight);
        btnDiscard.SetBounds(btnApply.Left - btnWidth - margin,
            btnY, btnWidth, btnHeight);

        /* The height left for the Value and Raw boxes, after the Comment   */
        /* row and the gaps, never less than 60 for either.                 */
        freeHeight = btnY - 12 - y - (rowHeight + gap) - gap;
        valueHeight = freeHeight * 2 / 5;
        if (valueHeight < 60)
        {   /* Short panel: keep a usable minimum height. */
            valueHeight = 60;
        }
        rawHeight = freeHeight - valueHeight;
        if (rawHeight < 60)
        {   /* Short panel: keep a usable minimum height. */
            rawHeight = 60;
        }

        /* Value row; the checkbox that stands in for the box is centred   */
        /* on its first line.                                              */
        txtValue.SetBounds(fieldLeft, y, fieldWidth, valueHeight);
        lblValue.Location = new Point(margin,
            y + (rowHeight - lblValue.Height) / 2);
        chkBoolValue.Location = new Point(fieldLeft,
            y + (rowHeight - chkBoolValue.Height) / 2);
        y += valueHeight + gap;

        /* Comment row. */
        EDIT_txtComment.SetBounds(fieldLeft, y, fieldWidth, rowHeight);
        EDIT_lblComment.Location = new Point(margin,
            y + (rowHeight - EDIT_lblComment.Height) / 2);
        y += rowHeight + gap;

        /* Raw row. */
        EDIT_txtRaw.SetBounds(fieldLeft, y, fieldWidth, rawHeight);
        EDIT_lblRaw.Location = new Point(margin,
            y + (rowHeight - EDIT_lblRaw.Height) / 2);
    }

    /*----------------------------------------------------------------------*/
    /* EDIT_MarkDirty:                                                      */
    /*                                                                      */
    /* Called when an editor's text or state changes.  When the USER made   */
    /* the change (not EDIT_LoadPanel), the panel now differs from the      */
    /* document; and since a Raw edit and the other fields cannot both be   */
    /* saved, the first one edited locks the other until Save or Discard.   */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     bRaw : the change was in the Raw box.                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the dirty flags are set and the other side locked.        */
    /*----------------------------------------------------------------------*/
    void EDIT_MarkDirty(bool bRaw)
    {
        if (suppressPanelDirty)
        {   /* Loaded by code: not an edit. */
            return;
        }

        if (bRaw && !editPanelDirty)
        {   /* The first edit is in the Raw box: lock the other fields. */
            EDIT_bRawDirty = true;
            txtKey.ReadOnly = true;
            cboType.Enabled = false;
            txtValue.ReadOnly = true;
            chkBoolValue.Enabled = false;
            EDIT_txtComment.ReadOnly = true;
        }
        else if (!bRaw && !editPanelDirty)
        {   /* The first edit is in the other fields: lock the Raw box. */
            EDIT_txtRaw.ReadOnly = true;
        }

        editPanelDirty = true;
    }

    /*------------------------------------------------------------------------*/
    /* VIEW: font size and the raw-text option                                */
    /*------------------------------------------------------------------------*/

    /*----------------------------------------------------------------------*/
    /* VIEW_ApplyFontSize:                                                  */
    /*                                                                      */
    /* Sets the font size used by the tree and the edit panel.  The         */
    /* panel's controls inherit their font from the panel, so one           */
    /* assignment covers them all, except the Raw box, which has a          */
    /* fixed-pitch font of the same size so the file's layout lines up.     */
    /* The panel is then laid out again for the new text sizes and the      */
    /* View menu's check mark moved.  The size is clamped to                */
    /* VIEW_FONTMIN..VIEW_FONTMAX.  The previous fonts are disposed once    */
    /* nothing uses them.                                                   */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     size : requested size, points.                                   */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : fontSize, viewFont, the tree, the panel and the menu      */
    /*            check marks are updated.                                  */
    /*----------------------------------------------------------------------*/
    void VIEW_ApplyFontSize(float size)
    {
        Font oldFont;                                    // font being replaced, disposed afterwards
        Font oldRawFont;                                 // Raw box font being replaced

        if (size < VIEW_FONTMIN)
        {   /* Too small to read: use the smallest allowed. */
            size = VIEW_FONTMIN;
        }

        if (size > VIEW_FONTMAX)
        {   /* Too large to be useful: use the largest allowed. */
            size = VIEW_FONTMAX;
        }

        fontSize = size;

        /* Swap in the new fonts, then dispose the old ones: the tree and   */
        /* panel no longer refer to them.                                   */
        oldFont = viewFont;
        oldRawFont = EDIT_fontRaw;
        viewFont = new Font(Control.DefaultFont.FontFamily, size);
        EDIT_fontRaw = new Font(FontFamily.GenericMonospace, size);
        tree.Font = viewFont;
        editPanel.Font = viewFont;
        EDIT_txtRaw.Font = EDIT_fontRaw;
        if (oldFont != null)
        {   /* There was a previous font (not on the very first call): release it. */
            oldFont.Dispose();
        }

        if (oldRawFont != null)
        {   /* And the previous Raw box font. */
            oldRawFont.Dispose();
        }

        /* Text sizes changed, so every position in the panel changed. */
        EDIT_LayoutPanel();

        /* Tick the preset matching the new size; none is ticked for a   */
        /* size that is not a preset, such as the 8.25 pt default.       */
        foreach (ToolStripMenuItem item in fontSizeItems)
        {
            item.Checked = (Math.Abs((float)item.Tag - fontSize) < 0.01f);
        }
    }

    /*------------------------------------------------------------------------*/
    /* VIEW_StepFontSize:                                                     */
    /*                                                                        */
    /* Moves to the next larger or next smaller preset size.  It works        */
    /* from any current size, including the 8.25 pt default that is not       */
    /* itself a preset.  At either end of the list nothing changes.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     direction : above 0 for larger, 0 or below for smaller.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the font size may change through VIEW_ApplyFontSize.        */
    /*------------------------------------------------------------------------*/
    void VIEW_StepFontSize(int direction)
    {
        int i;

        if (direction > 0)
        {   /* Larger: the first preset above the current size (0.01 pt tolerance for float noise). */
            for (i = 0; i < VIEW_FONTSIZES.Length; i++)
            {
                if (VIEW_FONTSIZES[i] > fontSize + 0.01f)
                {   /* Found it. */
                    VIEW_ApplyFontSize(VIEW_FONTSIZES[i]);
                    return;
                }
            }
        }
        else
        {   /* Smaller: the last preset below the current size. */
            for (i = VIEW_FONTSIZES.Length - 1; i >= 0; i--)
            {
                if (VIEW_FONTSIZES[i] < fontSize - 0.01f)
                {   /* Found it. */
                    VIEW_ApplyFontSize(VIEW_FONTSIZES[i]);
                    return;
                }
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* VIEW_ToggleShowRaw:                                                    */
    /*                                                                        */
    /* View > Show Raw Text in Tree: switches every tree label between        */
    /* the friendly form ("key: value") and the entry's first line of         */
    /* text exactly as in the file.  The setting is saved with the window     */
    /* settings (CFG_Save).                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the menu tick and every label change.                       */
    /*------------------------------------------------------------------------*/
    void VIEW_ToggleShowRaw()
    {
        VIEW_tsmShowRaw.Checked = !VIEW_tsmShowRaw.Checked;
        tree.Vtv_SetShowRaw(VIEW_tsmShowRaw.Checked);
    }

    /*------------------------------------------------------------------------*/
    /* VIEW_ToggleColorKeys:                                                  */
    /*                                                                        */
    /* View > Color Keys: switches the tree between the normal text colour    */
    /* throughout and the colour scheme -- names (keys,                       */
    /* indexes, "root", section and key paths) by role, values by type, as    */
    /* chosen in View > Colors... (VIEW_ShowColors).  Repeated keys stay      */
    /* red.  The setting is saved with the window settings (CFG_Save).        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the menu tick changes and the tree repaints.                */
    /*------------------------------------------------------------------------*/
    void VIEW_ToggleColorKeys()
    {
        VIEW_tsmColorKeys.Checked = !VIEW_tsmColorKeys.Checked;
        tree.Vtv_SetColorKeys(VIEW_tsmColorKeys.Checked);
    }

    /*-------------------------------------------------------------------------*/
    /* VIEW_HookColors, DLG_HookColor:                                         */
    /*                                                                         */
    /* Partial methods: places where the test harness works the Colors         */
    /* dialog and answers the colour picker instead of the user.  Voorhees.    */
    /* exe has no body for them, so the compiler drops the calls and the real  */
    /* windows always show; the harness (tools\VoorheesHarness.cs) drives the  */
    /* dialog through its methods (VoorheesColorsForm.Vco_PickRow,             */
    /* Vco_Reset, Vco_Accept, Vco_Cancel) without showing it -- no synthetic   */
    /* input -- and answers each pick with a scripted colour.                  */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     dlg         : VIEW_HookColors: the Colors dialog, built, not shown. */
    /*     sRole       : DLG_HookColor: the role being picked for              */
    /*                   (VoorheesColorsForm.VCO_ROLEKEYS).                    */
    /*     colorNow    : DLG_HookColor: its colour now.                        */
    /*     bAnswered   : set to true when the hook did the work.               */
    /*     bPicked     : DLG_HookColor: true for a colour picked, false for    */
    /*                   Cancel.                                               */
    /*     colorAnswer : DLG_HookColor: the colour picked.                     */
    /*                                                                         */
    /* Returns:                                                                */
    /*     void : the outcome, if any, is in the ref arguments (and in the     */
    /*            dialog's state).                                             */
    /*-------------------------------------------------------------------------*/
    partial void VIEW_HookColors(VoorheesColorsForm dlg, ref bool bAnswered);
    partial void DLG_HookColor(string sRole, Color colorNow, ref bool bAnswered,
        ref bool bPicked, ref Color colorAnswer);

    /*------------------------------------------------------------------------*/
    /* VIEW_ShowColors:                                                       */
    /*                                                                        */
    /* View > Colors...: the Colors dialog                                    */
    /* (VoorheesColorsForm) with the tree's colours now; on OK its colours    */
    /* replace the tree's, the tree repaints, and they are stored in the      */
    /* registry (CFG_SaveColors).  Cancel changes nothing.  Modal: a short    */
    /* choice that is answered and closed (the modeless preference allows     */
    /* that); the test harness can work it instead (VIEW_HookColors).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the colours are changed and saved, or nothing changes.      */
    /*------------------------------------------------------------------------*/
    void VIEW_ShowColors()
    {
        VoorheesColorsForm dlg;                          // the dialog
        bool bAnswered;                                  // the harness worked it
        bool bSaved;                                     // OK was pressed
        Color[] arrNames;                                // the chosen name colours
        Color[] arrValues;                               // the chosen value colours

        dlg = VoorheesColorsForm.Vco_Create(Font, tree.Vtv_arrNameColors,
            tree.Vtv_arrValueColors, VIEW_PickColor);
        using (dlg)
        {
            bAnswered = false;
            VIEW_HookColors(dlg, ref bAnswered);
            if (!bAnswered)
            {   /* The user's choice. */
                dlg.ShowDialog(this);
            }

            /* The outcome, read before the dialog is gone. */
            bSaved = dlg.Vco_bSaved;
            arrNames = dlg.Vco_arrNameColors;
            arrValues = dlg.Vco_arrValueColors;
        }

        if (!bSaved)
        {   /* Cancelled: nothing changes. */
            return;
        }

        /* The tree's tables take the chosen colours (the tables      */
        /* themselves stay: the tree holds them), shown at once and   */
        /* kept for next time.                                        */
        Array.Copy(arrNames, tree.Vtv_arrNameColors, tree.Vtv_arrNameColors.Length);
        Array.Copy(arrValues, tree.Vtv_arrValueColors, tree.Vtv_arrValueColors.Length);
        tree.Invalidate();
        CFG_SaveColors();
    }

    /*------------------------------------------------------------------------*/
    /* VIEW_PickColor:                                                        */
    /*                                                                        */
    /* The Colors dialog's colour picker (a VoorheesColorPick): the standard  */
    /* Windows colour dialog (ColorDialog, fully open so any colour can be    */
    /* mixed), starting at the role's colour now, owned by the Colors dialog; */
    /* or the test harness's answer (DLG_HookColor).  The common dialog       */
    /* reports OK only through its DialogResult, which is therefore what is   */
    /* read here (the manual-flag rule is for Voorhees' own forms).           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     owner       : the window the picker belongs to.                    */
    /*     sRole       : the role being picked for (VCO_ROLEKEYS).            */
    /*     colorNow    : its colour now.                                      */
    /*     colorPicked : receives the colour picked (colorNow when            */
    /*                   cancelled).                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when a colour was picked, false for Cancel.            */
    /*------------------------------------------------------------------------*/
    bool VIEW_PickColor(IWin32Window owner, string sRole, Color colorNow,
        out Color colorPicked)
    {
        ColorDialog dlgColor;                            // the Windows colour dialog
        bool bAnswered;                                  // the harness answered
        bool bPicked;                                    // a colour was picked
        Color colorAnswer;                               // the colour picked

        bAnswered = false;
        bPicked = false;
        colorAnswer = colorNow;
        DLG_HookColor(sRole, colorNow, ref bAnswered, ref bPicked, ref colorAnswer);
        if (bAnswered)
        {   /* Answered by the harness: no window. */
            colorPicked = colorAnswer;
            return(bPicked);
        }

        colorPicked = colorNow;
        dlgColor = new ColorDialog();
        using (dlgColor)
        {
            dlgColor.Color = colorNow;
            dlgColor.FullOpen = true;
            dlgColor.AnyColor = true;
            if (dlgColor.ShowDialog(owner) == DialogResult.OK)
            {   /* Picked: opaque, as the dialog gives it. */
                colorPicked = Color.FromArgb(255, dlgColor.Color);
                return(true);
            }
        }

        /* Cancelled: the colour stays. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* INIT_StatusStrip:                                                      */
    /*                                                                        */
    /* Builds the status bar: the open file's full path, stretched to         */
    /* fill, then the document's format, a "Contains comments" marker and a   */
    /* "Modified" marker on the right.  STATE_UpdateTitle keeps them current. */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : statusStrip and its labels are created and added.           */
    /*------------------------------------------------------------------------*/
    void INIT_StatusStrip()
    {
        statusStrip = new StatusStrip();

        /* File path: Spring makes it take all the width the markers   */
        /* do not, left-aligned.                                       */
        lblFilePath = new ToolStripStatusLabel();
        lblFilePath.Spring = true;
        lblFilePath.TextAlign = ContentAlignment.MiddleLeft;

        /* The document's format ("JSON", "INI" ...). */
        STATE_lblFormat = new ToolStripStatusLabel();
        STATE_lblFormat.Text = "";

        /* "Contains comments", empty while the document has none. */
        STATE_lblComments = new ToolStripStatusLabel();
        STATE_lblComments.Text = "";

        /* "Modified" marker, empty while the document is saved. */
        lblModified = new ToolStripStatusLabel();
        lblModified.Text = "";
        lblModified.Width = 80;

        statusStrip.Items.Add(lblFilePath);
        statusStrip.Items.Add(STATE_lblFormat);
        statusStrip.Items.Add(STATE_lblComments);
        statusStrip.Items.Add(lblModified);
        Controls.Add(statusStrip);
    }

    /*------------------------------------------------------------------------*/
    /* INIT_ContextMenu:                                                      */
    /*                                                                        */
    /* Attaches an empty right-click menu to the tree.  Its items depend      */
    /* on the entry clicked, so CTX_Opening builds them each time the         */
    /* menu opens.                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : treeContextMenu is created and attached to the tree.        */
    /*------------------------------------------------------------------------*/
    void INIT_ContextMenu()
    {
        treeContextMenu = new ContextMenuStrip();
        treeContextMenu.Opening += CTX_Opening;
        tree.ContextMenuStrip = treeContextMenu;
    }

    /*------------------------------------------------------------------------*/
    /* CTX: the tree's right-click menu                                       */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* CTX_Opening:                                                           */
    /*                                                                        */
    /* Builds the right-click menu for the selected entry just before it      */
    /* shows.  Any changes typed into the edit panel are settled first.       */
    /* The actions are handed the entry's POSITION PATH, which names the      */
    /* same entry until the document changes.                                 */
    /*     o Objects and arrays get Add Child (one item per kind the          */
    /*       format offers there -- Vf_ChildKinds -- and Comment).            */
    /*     o Object members get Rename Key.                                   */
    /*     o Entries that name another file (Vf_IncludePath: a Klipper-family */
    /*       include, in a saved document) get Open Included File.            */
    /*     o Every entry gets Copy; objects and arrays also get Paste once    */
    /*       something has been copied.                                       */
    /*     o Top-level entries get the top-level additions (CTX_AddTopLevel:  */
    /*       Add Top-Level Comment for JSON, an Add Top-Level submenu where   */
    /*       the format has top-level kinds, such as INI's sections), and so  */
    /*       does a right click on empty space or an empty document, where    */
    /*       they are all the menu offers.                                    */
    /*     o Everything the format lets go gets Delete (Vf_RemoveProblem:     */
    /*       JSON keeps the top value).                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sender : the context menu.                                         */
    /*     e      : set e.Cancel to keep the menu closed.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : treeContextMenu is refilled, or the opening cancelled.      */
    /*------------------------------------------------------------------------*/
    void CTX_Opening(object sender, System.ComponentModel.CancelEventArgs e)
    {
        TreeNode treeNode;                               // node the menu is for
        VoorheesItem item;                               // its entry
        ToolStripMenuItem addMenu;                       // Add Child submenu
        List<int> path;                                  // position path handed to the actions
        List<int> lstKinds;                              // value kinds Add Child offers here
        bool bContainer;                                 // the entry is a container (object, array)
        bool bEmptySpace;                                // opened by a right click on empty space
        int i;

        treeContextMenu.Items.Clear();
        bEmptySpace = CTX_bEmptySpace;
        CTX_bEmptySpace = false;

        if (!EDIT_ConfirmPendingPanel())
        {   /* User chose Cancel on the unsaved-panel question: no menu. */
            e.Cancel = true;
            return;
        }

        treeNode = tree.SelectedNode;
        if (bEmptySpace || treeNode == null || treeNode.Tag == null)
        {   /* Empty space, or nothing selected (an empty document): only the top-level additions. */
            CTX_AddTopLevel();

            /* WinForms starts Opening with Cancel already true when the   */
            /* menu held no items when it was asked to open -- the very    */
            /* first right-click of a session, on an empty New As          */
            /* document -- so the items just added would never show.       */
            e.Cancel = (treeContextMenu.Items.Count == 0);
            return;
        }

        path = VoorheesTreeView.Vtv_PathOf(treeNode);
        item = doc.Vd_ItemAt(path);
        if (item == null)
        {   /* Tree out of step with the document: offer nothing rather than act on the wrong entry. */
            e.Cancel = true;
            return;
        }

        bContainer = item.Vi_IsValue() && item.Vi_node.Vn_IsContainer();

        if (bContainer)
        {   /* A container: it can take a new child of each kind its format offers there, or a comment. */
            addMenu = new ToolStripMenuItem("Add Child");
            lstKinds = VoorheesFormat.Vf_ChildKinds(doc, item.Vi_node);
            for (i = 0; i < lstKinds.Count; i++)
            {
                CTX_AddKindItem(addMenu, path, lstKinds[i]);
            }

            /* A comment entry, set apart. */
            if (lstKinds.Count > 0)
            {   /* Kinds above it: a separator between them and Comment. */
                addMenu.DropDownItems.Add(new ToolStripSeparator());
            }
            addMenu.DropDownItems.Add("Comment...", null,
                delegate { CTX_AddChild(path, EDIT_KINDCOMMENT); });
            treeContextMenu.Items.Add(addMenu);
        }

        if (item.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* Object member: offer Rename Key. */
            treeContextMenu.Items.Add("Rename Key...", null,
                delegate { CTX_RenameKey(path); });
        }

        if (VoorheesFormat.Vf_IncludePath(doc, item) != null)
        {   /* An entry that names another file (a Klipper-family include): offer to open it. */
            treeContextMenu.Items.Add("Open Included File", null,
                delegate { CTX_OpenIncluded(path); });
        }

        /* Anything can be copied. */
        treeContextMenu.Items.Add("Copy", null,
            delegate { CTX_Copy(path); });

        if (clipboardItem != null && bContainer)
        {   /* Something was copied and this entry can hold it: offer Paste. */
            treeContextMenu.Items.Add("Paste", null,
                delegate { CTX_Paste(path); });
        }

        if (path.Count == 1)
        {   /* A top-level entry: things can be added at the top level too. */
            CTX_AddTopLevel();
        }

        if (VoorheesFormat.Vf_RemoveProblem(doc,
            doc.Vd_NodeAt(path.GetRange(0, path.Count - 1)), item) == null)
        {   /* The format lets it go (JSON keeps the top value, the document's one value): offer Delete, set apart. */
            treeContextMenu.Items.Add(new ToolStripSeparator());
            treeContextMenu.Items.Add("Delete", null,
                delegate { CTX_Delete(path); });
        }

        /* Shown whenever it has items (see the empty-space case above for   */
        /* why WinForms may have started with Cancel set).                   */
        e.Cancel = (treeContextMenu.Items.Count == 0);
    }

    /*------------------------------------------------------------------------*/
    /* CTX_AddTopLevel:                                                       */
    /*                                                                        */
    /* Adds the top-level additions to the right-click menu: where the        */
    /* format offers kinds at the top level (Vf_ChildKinds of the document:   */
    /* INI's entries and sections), an "Add Top-Level" submenu with them and  */
    /* Comment...; where it offers none (JSON, whose document holds its one   */
    /* value and comments), "Add Top-Level Comment...".                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : treeContextMenu gains one item.                             */
    /*------------------------------------------------------------------------*/
    void CTX_AddTopLevel()
    {
        ToolStripMenuItem addMenu;                       // Add Top-Level submenu
        List<int> lstKinds;                              // kinds the top level takes
        List<int> lstDocument;                           // the document's path (empty)
        int i;

        lstDocument = new List<int>();
        lstKinds = VoorheesFormat.Vf_ChildKinds(doc, doc.Vd_root);
        if (lstKinds.Count == 0)
        {   /* Comments only (JSON). */
            treeContextMenu.Items.Add("Add Top-Level Comment...", null,
                delegate { CTX_AddChild(lstDocument, EDIT_KINDCOMMENT); });
            return;
        }

        /* Each kind, then Comment, set apart. */
        addMenu = new ToolStripMenuItem("Add Top-Level");
        for (i = 0; i < lstKinds.Count; i++)
        {
            CTX_AddKindItem(addMenu, lstDocument, lstKinds[i]);
        }
        addMenu.DropDownItems.Add(new ToolStripSeparator());
        addMenu.DropDownItems.Add("Comment...", null,
            delegate { CTX_AddChild(lstDocument, EDIT_KINDCOMMENT); });
        treeContextMenu.Items.Add(addMenu);
    }

    /*------------------------------------------------------------------------*/
    /* CTX_AddKindItem:                                                       */
    /*                                                                        */
    /* Adds one kind's entry to the Add Child submenu, named as the format    */
    /* names the kind.  A function of its own so that each menu entry's       */
    /* click handler captures its own kind: a handler made inside the menu's  */
    /* loop would capture the loop variable itself, and every entry would add */
    /* the kind the loop ended on.                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     addMenu : the Add Child submenu.                                   */
    /*     path    : the container's position path.                           */
    /*     iKind   : the kind this entry adds.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : addMenu gains one entry.                                    */
    /*------------------------------------------------------------------------*/
    void CTX_AddKindItem(ToolStripMenuItem addMenu, List<int> path, int iKind)
    {
        addMenu.DropDownItems.Add(VoorheesFormat.Vf_KindName(doc.Vd_iFormat,
            iKind), null, delegate { CTX_AddChild(path, iKind); });
    }

    /*------------------------------------------------------------------------*/
    /* CTX_AddChild:                                                          */
    /*                                                                        */
    /* Adds an empty value of the chosen kind (or a comment entry, whose      */
    /* words are asked for first) at the end of a container, or a comment at  */
    /* the end of the document, and selects it.  A new object member gets     */
    /* the first free name out of newKey, newKey1 ... (Vd_ActAddChild), or,   */
    /* where its format asks for the name first (Vf_NameQuestion: an          */
    /* include's file, a registry key's hive and path), the name given        */
    /* (DLG_AskName), in the same undo step.  The                             */
    /* cursor is then put where the new entry needs editing: the Key box for  */
    /* an object member, the Value box otherwise.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     containerPath : path of the container (empty for the document).    */
    /*     iKind         : a value kind of the document's format, or          */
    /*                     EDIT_KINDCOMMENT (-1, which is also what           */
    /*                     Vd_ActAddChild takes for a comment entry).         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document gains an entry (one undo step) and it is       */
    /*            selected.                                                   */
    /*------------------------------------------------------------------------*/
    void CTX_AddChild(List<int> containerPath, int iKind)
    {
        VoorheesEditStep step;                           // the action's undo step
        List<int> newPath;                               // where the new entry went
        string sWords;                                   // a comment's words
        string sName;                                    // a name asked for first (an include's file, a registry key's path), or null
        VoorheesNameQuestion question;                   // the format's question for that name, or null
        string sError;                                   // why the action was refused

        sWords = null;
        sName = null;
        question = null;
        if (iKind == EDIT_KINDCOMMENT)
        {   /* A comment: ask for its words. */
            sWords = DLG_InputBox("Add Comment", "Comment:", "");
            if (sWords == null)
            {   /* Cancelled. */
                return;
            }
        }
        else
        {   /* An entry: the format may want its name first. */
            question = VoorheesFormat.Vf_NameQuestion(doc, iKind);
        }

        if (question != null)
        {   /* Asked first (a Klipper-family include's file; a registry key's hive and path). */
            sName = DLG_AskName(question);
            if (sName == null)
            {   /* Cancelled. */
                return;
            }
        }

        step = doc.Vd_BeginStep(null);
        sError = doc.Vd_ActAddChild(step, containerPath, iKind, sWords,
            out newPath);
        if (sError == null && sName != null)
        {   /* Added with a placeholder: give it the name asked for, in the same step (refused: all of it is taken back). */
            sError = doc.Vd_ActRename(step, newPath, sName);
        }
        step.Ves_lstFocus = newPath;
        if (!DOC_Finish(step, sError))
        {   /* Refused (the user has been told why). */
            return;
        }

        /* Focus the right editor only after the context menu has         */
        /* finished closing: done straight away, the closing menu hands   */
        /* focus back to the tree.                                        */
        BeginInvoke((MethodInvoker)delegate
        {
            if (txtKey.Enabled)
            {   /* New object member: its placeholder key is the first thing to change. */
                txtKey.Focus();
                txtKey.SelectAll();
            }
            else if (txtValue.Visible && txtValue.Enabled)
            {   /* New array item or comment: go to the value. */
                txtValue.Focus();
                txtValue.SelectAll();
            }
        });
    }

    /*------------------------------------------------------------------------*/
    /* CTX_OpenIncluded:                                                      */
    /*                                                                        */
    /* Open Included File (decision K3): opens the file an include names, as  */
    /* the same format as this document, after the usual save prompt.  A      */
    /* name with wildcards ("*", "?", "[") opens the Open dialog in that      */
    /* folder, filtered by the pattern, to pick one of the matches; a plain   */
    /* name that does not exist is reported.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     path : path of the include entry.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the included file becomes the document, unless the user     */
    /*            cancelled, it does not exist, or it could not be read.      */
    /*------------------------------------------------------------------------*/
    void CTX_OpenIncluded(List<int> path)
    {
        OpenFileDialog dlg;                              // file picker for a pattern
        VoorheesItem item;                               // the include
        string sTarget;                                  // the file or pattern it names
        string sFolder;                                  // the pattern's folder
        string sPattern;                                 // the pattern's file part
        string sChosen;                                  // the file picked
        bool bAnswered;                                  // the test harness picked it
        int iFormat;                                     // this document's format, for the included file

        item = doc.Vd_ItemAt(path);
        if (item == null)
        {   /* Gone. */
            return;
        }
        sTarget = VoorheesFormat.Vf_IncludePath(doc, item);
        if (sTarget == null)
        {   /* Names no file (or this document has no folder yet). */
            return;
        }
        iFormat = doc.Vd_iFormat;

        if (sTarget.IndexOfAny(new char[] { '*', '?', '[' }) < 0)
        {   /* One file: open it if it is there. */
            if (!File.Exists(sTarget))
            {   /* Missing: say so. */
                DLG_Message("The included file was not found:\r\n\r\n" + sTarget,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!FILE_ConfirmDiscard())
            {   /* User cancelled at the save prompt. */
                return;
            }
            FILE_LoadFromPath(sTarget, iFormat);
            return;
        }

        /* A pattern: pick one of its matches. */
        if (!FILE_ConfirmDiscard())
        {   /* User cancelled at the save prompt. */
            return;
        }

        bAnswered = false;
        sChosen = null;
        DLG_HookFile(false, ref bAnswered, ref sChosen);
        if (bAnswered)
        {   /* Chosen by the harness: open it, unless it chose nothing. */
            if (sChosen != null)
            {   /* A file: open it. */
                FILE_LoadFromPath(sChosen, iFormat);
            }
            return;
        }

        sFolder = Path.GetDirectoryName(sTarget);
        sPattern = Path.GetFileName(sTarget);
        if (sFolder.IndexOfAny(new char[] { '*', '?', '[' }) >= 0)
        {   /* Wildcards in the folder too: start beside this document. */
            sFolder = Path.GetDirectoryName(doc.Vd_sPath);
        }
        if (sPattern.IndexOf('[') >= 0)
        {   /* A character class the dialog can't filter by: show everything. */
            sPattern = "*.*";
        }

        dlg = new OpenFileDialog();
        using (dlg)
        {
            dlg.Title = "Open Included File";
            dlg.Filter = "Included files (" + sPattern + ")|" + sPattern
                + "|All files (*.*)|*.*";
            dlg.InitialDirectory = sFolder;
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {   /* Dialog cancelled: nothing to open. */
                return;
            }
            sChosen = dlg.FileName;
        }

        FILE_LoadFromPath(sChosen, iFormat);
    }

    /*------------------------------------------------------------------------*/
    /* CTX_RenameKey:                                                         */
    /*                                                                        */
    /* Asks for a new key for an object member and renames it in place;       */
    /* the member keeps its position, value and layout.  An empty key, or     */
    /* one already used in the object, is refused (Vd_ActRename), so no       */
    /* new duplicate key is ever created.  The question is the plain box      */
    /* holding the key, unless the format has one of its own                  */
    /* (Vf_RenameQuestion: a registry key's hive drop-down and path).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     path : path of the member to rename.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the key may change (one undo step).                         */
    /*------------------------------------------------------------------------*/
    void CTX_RenameKey(List<int> path)
    {
        VoorheesItem item;                               // the member
        VoorheesEditStep step;                           // the action's undo step
        VoorheesNameQuestion question;                   // the format's own question, or null
        string newKey;                                   // key typed by the user

        item = doc.Vd_ItemAt(path);
        if (item == null || item.Vi_iKind != VoorheesItem.VI_MEMBER)
        {   /* Gone, or not a member: nothing to rename. */
            return;
        }

        question = VoorheesFormat.Vf_RenameQuestion(doc, item);
        if (question != null)
        {   /* The format's question (a registry key: hive and path). */
            newKey = DLG_AskName(question);
        }
        else
        {   /* The plain box, holding the key as it is. */
            newKey = DLG_InputBox("Rename Key", "New key name:", item.Vi_sKey);
        }
        if (newKey == null || newKey == item.Vi_sKey)
        {   /* Cancelled, or left unchanged: nothing to do. */
            return;
        }

        step = doc.Vd_BeginStep(path);
        DOC_Finish(step, doc.Vd_ActRename(step, path, newKey));
    }

    /*------------------------------------------------------------------------*/
    /* CTX_Copy:                                                              */
    /*                                                                        */
    /* Puts a deep copy of an entry on the editor's own clipboard, for        */
    /* Paste (not the Windows clipboard).  A copy, so later edits to the      */
    /* original do not show up in what gets pasted.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     path : path of the entry to copy.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : clipboardItem is replaced.                                  */
    /*------------------------------------------------------------------------*/
    void CTX_Copy(List<int> path)
    {
        VoorheesItem item;                               // the entry

        item = doc.Vd_ItemAt(path);
        if (item == null)
        {   /* Gone: nothing to copy. */
            return;
        }

        clipboardItem = item.Vi_CloneDeep();
        CTX_iClipboardFormat = doc.Vd_iFormat;
    }

    /*------------------------------------------------------------------------*/
    /* CTX_Paste:                                                             */
    /*                                                                        */
    /* Adds a fresh copy of the clipboard entry at the end of an object       */
    /* or array, laid out in this document's style, and selects it            */
    /* (Vd_ActPaste: a member keeps its key if free, else the next free       */
    /* variant; members and elements convert as needed).                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     containerPath : path of the object or array to paste into.         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document gains an entry (one undo step).                */
    /*------------------------------------------------------------------------*/
    void CTX_Paste(List<int> containerPath)
    {
        VoorheesEditStep step;                           // the action's undo step
        List<int> newPath;                               // where the copy went
        string sError;                                   // why the action was refused

        if (clipboardItem == null)
        {   /* Nothing has been copied. */
            return;
        }

        step = doc.Vd_BeginStep(null);
        sError = doc.Vd_ActPaste(step, containerPath, clipboardItem,
            CTX_iClipboardFormat, out newPath);
        step.Ves_lstFocus = newPath;
        DOC_Finish(step, sError);
    }

    /*------------------------------------------------------------------------*/
    /* CTX_Delete:                                                            */
    /*                                                                        */
    /* Removes an entry by POSITION, so deleting one copy of a duplicated     */
    /* key leaves the other copies alone.  Afterwards the selection lands     */
    /* on whatever now has the same position (the next entry) or else on      */
    /* the nearest ancestor.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     path : path of the entry to delete.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the entry is removed (one undo step).                       */
    /*------------------------------------------------------------------------*/
    void CTX_Delete(List<int> path)
    {
        VoorheesEditStep step;                           // the action's undo step

        step = doc.Vd_BeginStep(path);
        DOC_Finish(step, doc.Vd_ActRemove(step, path));
    }

    /*------------------------------------------------------------------------*/
    /* FILE: new, open, close, save                                           */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* FILE_New:                                                              */
    /*                                                                        */
    /* Replaces the document with a new, empty one of a format, after         */
    /* offering to save unsaved changes: File > New (the current format) and  */
    /* File > New As.  Each format makes its own (VoorheesDocument.           */
    /* Vd_CreateNew: JSON an empty {} object, INI an empty file), in its      */
    /* default style -- UTF-8 without a BOM and CRLF for both.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VoorheesFormat.VF_ format.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document is replaced, unless the user cancelled.        */
    /*------------------------------------------------------------------------*/
    void FILE_New(int iFormat)
    {
        if (doc != null && !FILE_ConfirmDiscard())
        {   /* User cancelled at the save prompt: keep the current document. */
            return;
        }

        DOC_SetDocument(VoorheesDocument.Vd_CreateNew(iFormat));
    }

    /*------------------------------------------------------------------------*/
    /* FILE_Close:                                                            */
    /*                                                                        */
    /* File > Close.  Closing leaves an empty document of the current format  */
    /* ready to build from scratch, which is exactly what File > New does,    */
    /* so this only exists to give the menu item its own name.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : as FILE_New.                                                */
    /*------------------------------------------------------------------------*/
    void FILE_Close()
    {
        FILE_New(doc.Vd_iFormat);
    }

    /*------------------------------------------------------------------------*/
    /* FILE_FormatSubmenu:                                                    */
    /*                                                                        */
    /* Builds File > New As or File > Reopen As: one item per format, each    */
    /* greyed until its module exists (VoorheesFormat.Vf_IsAvailable).        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText   : the submenu's caption.                                   */
    /*     bReopen : true for Reopen As (the open file read again as the      */
    /*               format), false for New As (a new document of it).        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     ToolStripMenuItem : the submenu, filled.                           */
    /*------------------------------------------------------------------------*/
    ToolStripMenuItem FILE_FormatSubmenu(string sText, bool bReopen)
    {
        ToolStripMenuItem submenu;                       // the submenu
        int iFormat;

        submenu = new ToolStripMenuItem(sText);
        for (iFormat = VoorheesFormat.VF_JSON; iFormat <= VoorheesFormat.VF_REG;
            iFormat++)
        {
            submenu.DropDownItems.Add(FILE_FormatItem(iFormat, bReopen));
        }

        /* One item per format. */
        return(submenu);
    }

    /*------------------------------------------------------------------------*/
    /* FILE_FormatItem:                                                       */
    /*                                                                        */
    /* One format's item in New As or Reopen As.  A function of its own so    */
    /* each item's click handler captures its own format (a handler made in   */
    /* the submenu's loop would capture the loop variable).                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ format.                                          */
    /*     bReopen : Reopen As rather than New As.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     ToolStripMenuItem : the item.                                      */
    /*------------------------------------------------------------------------*/
    ToolStripMenuItem FILE_FormatItem(int iFormat, bool bReopen)
    {
        ToolStripMenuItem item;                          // the item

        if (bReopen)
        {   /* Read the open file again as this format. */
            item = new ToolStripMenuItem(VoorheesFormat.Vf_Name(iFormat), null,
                delegate { FILE_ReopenAs(iFormat); });
        }
        else
        {   /* A new document of this format. */
            item = new ToolStripMenuItem(VoorheesFormat.Vf_Name(iFormat), null,
                delegate { FILE_New(iFormat); });
        }
        item.Enabled = VoorheesFormat.Vf_IsAvailable(iFormat);

        /* Greyed until the format can be read and written. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* FILE_ReopenAs:                                                         */
    /*                                                                        */
    /* File > Reopen As: the open file read again, from disk, as a chosen     */
    /* format (when a name led to the wrong dialect, or                       */
    /* a file has no telling name), after offering to save unsaved changes.   */
    /* A file it cannot be read as leaves the current document as it is.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ format to read it as.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document is the file read as that format, unless the    */
    /*            user cancelled or it could not be read.                     */
    /*------------------------------------------------------------------------*/
    void FILE_ReopenAs(int iFormat)
    {
        string sPath;                                    // the open file

        sPath = doc.Vd_sPath;
        if (sPath == null || !FILE_ConfirmDiscard())
        {   /* No file to read again, or the user cancelled at the save prompt. */
            return;
        }

        FILE_LoadFromPath(sPath, iFormat);
    }

    /*------------------------------------------------------------------------*/
    /* FILE_Open:                                                             */
    /*                                                                        */
    /* Offers to save unsaved changes, asks for a file (starting in the       */
    /* current file's folder) and loads it: as the format of the dialog       */
    /* entry chosen, or -- for "All supported files" and "All files" -- the   */
    /* format its name or contents show (Vf_OpenDialogFormat, Vf_Detect).     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the chosen file becomes the document, unless the user       */
    /*            cancelled or the file could not be read.                    */
    /*------------------------------------------------------------------------*/
    void FILE_Open()
    {
        OpenFileDialog dlg;                              // file picker
        string path;                                     // file chosen
        bool bAnswered;                                  // the test harness chose the file
        int iFormat;                                     // the format chosen, or VF_DETECT

        if (!FILE_ConfirmDiscard())
        {   /* User cancelled at the save prompt: keep the current document. */
            return;
        }

        bAnswered = false;
        path = null;
        DLG_HookFile(false, ref bAnswered, ref path);
        if (bAnswered)
        {   /* Chosen by the harness: open it (format detected), unless it chose nothing. */
            if (path != null)
            {   /* A file: open it. */
                FILE_LoadFromPath(path, VoorheesFormat.VF_DETECT);
            }
            return;
        }

        dlg = new OpenFileDialog();
        using (dlg)
        {
            dlg.Filter = VoorheesFormat.Vf_OpenDialogFilter();
            dlg.Title = "Open File";
            if (doc.Vd_sPath != null)
            {   /* A file is open: start in its folder. */
                dlg.InitialDirectory = Path.GetDirectoryName(doc.Vd_sPath);
            }

            if (dlg.ShowDialog(this) != DialogResult.OK)
            {   /* Dialog cancelled: nothing to open. */
                return;
            }
            path = dlg.FileName;
            iFormat = VoorheesFormat.Vf_OpenDialogFormat(dlg.FilterIndex);
        }

        FILE_LoadFromPath(path, iFormat);
    }

    /*------------------------------------------------------------------------*/
    /* FILE_LoadFromPath:                                                     */
    /*                                                                        */
    /* Reads and parses a file and makes it the document.  The reading and    */
    /* parsing (FILE_ReadDocument: the format given, or the one its name or   */
    /* contents show) run on a worker thread behind the progress dialog,      */
    /* which appears only if they take longer than PROG_SHOWDELAYMS; the      */
    /* document, tree and messages are then set up here on the UI thread.     */
    /* What the format says the user should know (Vf_OpenWarnings: repeated   */
    /* keys, which other programs reduce to one copy) is shown after it is    */
    /* open.  Unreadable or invalid files leave the current document          */
    /* unchanged.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     path    : the file to load.                                        */
    /*     iFormat : the VoorheesFormat.VF_ format to read it as, or          */
    /*               VF_DETECT to work it out.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true if the file was loaded; false if it could not be       */
    /*            read or parsed (the user has been told why).                */
    /*------------------------------------------------------------------------*/
    bool FILE_LoadFromPath(string path, int iFormat)
    {
        VoorheesFileData data;                           // the file as read and parsed
        Exception exWork;                                // an unexpected failure in the worker
        Stopwatch swTime;                                // times the load for the debug log
        List<string> lstWarnings;                        // what the format says about the file
        int i;

        swTime = Stopwatch.StartNew();
        data = null;

        /* Read and parse on the worker.  It touches no controls: its   */
        /* whole result comes back in data.                             */
        exWork = BusyModalForm.Run(this, "Opening file",
            Path.GetFileName(path),
            delegate { data = FILE_ReadDocument(path, iFormat); },
            "Voorhees", PROG_SHOWDELAYMS);

        if (exWork != null || data == null)
        {   /* Something FILE_ReadDocument did not foresee: report it as a read failure. */
            DLG_Message("Failed to read file:\r\n" + PROG_Describe(exWork),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return(false);
        }

        if (data.Vfd_bReadFailed)
        {   /* Missing, locked or unreadable file: say so and keep the current document. */
            DLG_Message("Failed to read file:\r\n" + data.Vfd_sError,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return(false);
        }

        if (data.Vfd_sError != null)
        {   /* Not JSON: say why (with line and column) and keep the current document. */
            DLG_Message("Couldn't open " + Path.GetFileName(path)
                + ":\r\n" + data.Vfd_sError,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return(false);
        }

        Program.APP_DebugLog("load " + path + ": "
            + data.Vfd_lBytes.ToString(CultureInfo.InvariantCulture)
            + " bytes read and parsed in "
            + swTime.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
            + " ms");

        DOC_SetDocument(data.Vfd_doc);

        Program.APP_DebugLog("load " + path + ": ready after "
            + swTime.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
            + " ms in total");

        /* What the format says the user should know before editing or   */
        /* saving (JSON: repeated keys, which most programs reduce to    */
        /* the last copy while Voorhees keeps them all), one message     */
        /* each.                                                         */
        lstWarnings = VoorheesFormat.Vf_OpenWarnings(data.Vfd_doc,
            Path.GetFileName(path), data.Vfd_iExtraCopies);
        for (i = 0; i < lstWarnings.Count; i++)
        {
            DLG_Message(lstWarnings[i], MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        /* The file is now the document. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* FILE_ReadDocument:                                                     */
    /*                                                                        */
    /* Reads and parses a file WITHOUT touching the editor, so it can run on  */
    /* a worker thread behind the progress dialog, and so the command line's  */
    /* --validate reads files exactly as the editor does.  The format is the  */
    /* one given, or else the one the file's name or contents show            */
    /* (VoorheesFormat.Vf_Detect); its module decodes the bytes (remembering  */
    /* the encoding and byte order mark), parses them strictly and            */
    /* losslessly, and works out the style for generated text                 */
    /* (VoorheesFormat.Vf_Load).  What the user should hear about (repeated   */
    /* keys, comments) is counted here too, on the worker, because it walks   */
    /* the whole document.                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     path    : the file to read.                                        */
    /*     iFormat : the VoorheesFormat.VF_ format to read it as, or          */
    /*               VF_DETECT to work it out.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesFileData : always returned.  Vfd_bReadFailed with          */
    /*                        Vfd_sError when the file could not be read;     */
    /*                        Vfd_sError alone when it is not valid in the    */
    /*                        format; otherwise Vfd_doc and what was found.   */
    /*------------------------------------------------------------------------*/
    public static VoorheesFileData FILE_ReadDocument(string path, int iFormat)
    {
        VoorheesFileData data;                           // what is learned about the file
        byte[] bytes;                                    // the file's raw contents
        string error;                                    // why parsing failed

        data = new VoorheesFileData();

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {   /* Missing, locked or unreadable file: nothing more to learn. */
            data.Vfd_bReadFailed = true;
            data.Vfd_sError = ex.Message;
            return(data);
        }
        data.Vfd_lBytes = bytes.LongLength;

        if (iFormat == VoorheesFormat.VF_DETECT)
        {   /* No format given: the name, else the contents, decide. */
            iFormat = VoorheesFormat.Vf_Detect(path, bytes);
        }

        data.Vfd_doc = VoorheesFormat.Vf_Load(iFormat, bytes, path, out error);
        if (data.Vfd_doc == null)
        {   /* Not valid in that format: keep the reason. */
            data.Vfd_sError = error;
            return(data);
        }

        data.Vfd_iExtraCopies = data.Vfd_doc.Vd_ExtraCopies();
        data.Vfd_bHasComments = data.Vfd_doc.Vd_HasComments();

        /* A parsed document and what was found in it. */
        return(data);
    }

    /*------------------------------------------------------------------------*/
    /* FILE_DescribeFormat:                                                   */
    /*                                                                        */
    /* A one-line summary of how a file read by FILE_ReadDocument is          */
    /* written, for --validate --verbose: size, encoding and whether it       */
    /* has a byte order mark, line endings, the format's own style            */
    /* (Vf_DescribeStyle: JSON's indentation, INI's delimiter and comment     */
    /* marker), and comments where they make a file non-standard.  Line       */
    /* endings and style are what was detected for generated text (a file     */
    /* with no line breaks reports the CRLF default).                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     data : a successfully parsed file.                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "1204 bytes, UTF-8 with BOM, CRLF, 4-space           */
    /*              indent".                                                  */
    /*------------------------------------------------------------------------*/
    public static string FILE_DescribeFormat(VoorheesFileData data)
    {
        VoorheesDocument fileDoc;                        // the document read
        string sEncoding;                                // encoding name, with BOM note
        string sIndent;                                  // the format's style description
        string sNewline;                                 // line ending name
        string sComments;                                // comments note

        fileDoc = data.Vfd_doc;
        sEncoding = fileDoc.Vd_encoding.WebName.ToUpperInvariant();
        if (fileDoc.Vd_bBom)
        {   /* The file starts with a byte order mark. */
            sEncoding += " with BOM";
        }

        /* The format's own style (JSON: indentation). */
        sIndent = VoorheesFormat.Vf_DescribeStyle(fileDoc);

        if (fileDoc.Vd_sNewline == "\n")
        {   /* Unix style. */
            sNewline = "LF";
        }
        else
        {   /* Windows style. */
            sNewline = "CRLF";
        }

        sComments = "";
        if (data.Vfd_bHasComments
            && VoorheesFormat.Vf_CommentsMakeNonStandard(fileDoc.Vd_iFormat))
        {   /* Comments where the format's standard has none (JSON). */
            sComments = ", contains comments";
        }

        /* Size, encoding, line ending, indentation, comments. */
        return(data.Vfd_lBytes.ToString(CultureInfo.InvariantCulture)
            + " bytes, " + sEncoding + ", " + sNewline + ", " + sIndent
            + sComments);
    }

    /*------------------------------------------------------------------------*/
    /* FILE_Save:                                                             */
    /*                                                                        */
    /* Saves to the open file, or asks for a name if the document has         */
    /* never been saved.  Changes typed into the panel are settled first.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true if the document was written; false if the user         */
    /*            cancelled or the write failed.                              */
    /*------------------------------------------------------------------------*/
    bool FILE_Save()
    {
        if (!EDIT_ConfirmPendingPanel())
        {   /* User cancelled at the unsaved-panel question. */
            return(false);
        }

        if (doc.Vd_sPath == null)
        {   /* Never saved: ask where. */
            return(FILE_SaveAs());
        }

        /* Write over the open file. */
        return(FILE_WriteToPath(doc.Vd_sPath));
    }

    /*------------------------------------------------------------------------*/
    /* FILE_SaveAs:                                                           */
    /*                                                                        */
    /* Saves under a name the user chooses.  The document takes the new       */
    /* name only after the write has succeeded, so a failed Save As           */
    /* leaves it attached to its old file.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true if the document was written; false if the user         */
    /*            cancelled or the write failed.                              */
    /*------------------------------------------------------------------------*/
    bool FILE_SaveAs()
    {
        SaveFileDialog dlg;                              // file picker
        string path;                                     // file chosen
        bool bAnswered;                                  // the test harness chose the file

        if (!EDIT_ConfirmPendingPanel())
        {   /* User cancelled at the unsaved-panel question. */
            return(false);
        }

        bAnswered = false;
        path = null;
        DLG_HookFile(true, ref bAnswered, ref path);
        if (bAnswered)
        {   /* Chosen by the harness: write it, unless it chose nothing. */
            if (path == null || !FILE_ConfirmOtherExtension(path))
            {   /* Nothing chosen, or another format's name declined: as if cancelled. */
                return(false);
            }
            return(FILE_WriteToPath(path));
        }

        dlg = new SaveFileDialog();
        using (dlg)
        {
            dlg.Filter = VoorheesFormat.Vf_DialogFilter(doc.Vd_iFormat);
            dlg.DefaultExt = VoorheesFormat.Vf_DefaultExtension(doc.Vd_iFormat);
            dlg.Title = "Save " + VoorheesFormat.Vf_Name(doc.Vd_iFormat) + " File";

            if (doc.Vd_sPath != null)
            {   /* A file is open: offer its name and folder. */
                dlg.FileName = Path.GetFileName(doc.Vd_sPath);
                dlg.InitialDirectory = Path.GetDirectoryName(doc.Vd_sPath);
            }

            if (dlg.ShowDialog(this) != DialogResult.OK)
            {   /* Dialog cancelled: nothing written. */
                return(false);
            }
            path = dlg.FileName;
        }

        if (!FILE_ConfirmOtherExtension(path))
        {   /* The name belongs to another format and the user said no. */
            return(false);
        }

        /* Written (the document takes the new name), or the user was told why not. */
        return(FILE_WriteToPath(path));
    }

    /*------------------------------------------------------------------------*/
    /* FILE_ConfirmOtherExtension:                                            */
    /*                                                                        */
    /* Save As keeps the document's format (converting between formats is     */
    /* not available yet), so a name whose                                    */
    /* extension belongs to ANOTHER format would give a file that does not    */
    /* match its name.  The user is asked first; a name that says nothing     */
    /* (or says this format) needs no question.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     path : the file chosen.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true to go ahead and write it.                              */
    /*------------------------------------------------------------------------*/
    bool FILE_ConfirmOtherExtension(string path)
    {
        int iNamed;                                      // the format the name says

        iNamed = VoorheesFormat.Vf_DetectFromPath(path);
        if (iNamed == VoorheesFormat.VF_DETECT || iNamed == doc.Vd_iFormat)
        {   /* The name agrees, or says nothing. */
            return(true);
        }

        /* A name of another format: ask. */
        return(DLG_Message("This document will be written as "
            + VoorheesFormat.Vf_Name(doc.Vd_iFormat) + ", though the name "
            + Path.GetFileName(path) + " is a "
            + VoorheesFormat.Vf_Name(iNamed) + " name (converting between "
            + "formats is not available yet).\r\n\r\nSave anyway?",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
    }

    /*----------------------------------------------------------------------*/
    /* FILE_WriteToPath:                                                    */
    /*                                                                      */
    /* Writes the document to disk: every unedited part exactly as it was   */
    /* read, edited parts in the document's style, in the file's own        */
    /* encoding.  Writing (FILE_WriteDocument) runs on a worker thread      */
    /* behind the progress dialog, which appears only if it takes longer    */
    /* than PROG_SHOWDELAYMS; the document is not changed while it runs,    */
    /* because the dialog is modal.  A successful save makes the document   */
    /* belong to the file, clears the modified flag and ends the undo       */
    /* history (undo lasts until the next save).                            */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     path : file to write.                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     bool : true on success; false if the write failed (the user      */
    /*            has been told why and the old file is untouched).         */
    /*----------------------------------------------------------------------*/
    bool FILE_WriteToPath(string path)
    {
        VoorheesDocument docToWrite;                     // the document, captured for the worker
        Exception exWrite;                               // why the write failed, or null
        Stopwatch swTime;                                // times the save for the debug log

        docToWrite = doc;
        swTime = Stopwatch.StartNew();
        exWrite = BusyModalForm.Run(this, "Saving file",
            Path.GetFileName(path),
            delegate { FILE_WriteDocument(docToWrite, path); },
            "Voorhees", PROG_SHOWDELAYMS);

        if (exWrite != null)
        {   /* Write failed: the old file is untouched; say why. */
            DLG_Message("Failed to write file:\r\n" + PROG_Describe(exWrite),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return(false);
        }

        Program.APP_DebugLog("save " + path + ": written in "
            + swTime.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
            + " ms");

        /* On disk now: the file is the document's, nothing unsaved, and   */
        /* undo restarts here.                                             */
        doc.Vd_MarkSaved(path);
        STATE_UpdateTitle();

        /* Saved. */
        return(true);
    }

    /*----------------------------------------------------------------------*/
    /* FILE_WriteDocument:                                                  */
    /*                                                                      */
    /* Turns a document into its file's bytes (VoorheesJson.Vjs_SaveBytes)  */
    /* and writes them, touching no controls, so it can run on the          */
    /* progress dialog's worker thread.  The bytes go to a temporary file   */
    /* beside the target first, which is then swapped into place, so a      */
    /* failed or interrupted save never leaves a half-written file behind;  */
    /* a temporary file left by a failure is removed.                       */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     fileDoc : the document.                                          */
    /*     path    : file to write.                                         */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the file is written; a failure is thrown to the caller    */
    /*            with the target file left as it was.                      */
    /*----------------------------------------------------------------------*/
    static void FILE_WriteDocument(VoorheesDocument fileDoc, string path)
    {
        byte[] bytes;                                    // the file's new contents
        string tempPath;                                 // temporary file beside the target

        bytes = VoorheesFormat.Vf_SaveBytes(fileDoc);

        tempPath = path + ".voorhees.tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);

            if (File.Exists(path))
            {   /* Replacing an existing file: swap the finished temp file into its place. */
                try
                {
                    File.Replace(tempPath, path, null, true);
                }
                catch (IOException)
                {   /* Some network and FAT drives cannot Replace: copy over the file instead. */
                    File.Copy(tempPath, path, true);
                    File.Delete(tempPath);
                }
            }
            else
            {   /* New file: just give the temp file its name. */
                File.Move(tempPath, path);
            }
        }
        catch
        {   /* Write failed: tidy up the temp file, then pass the failure on. */
            try
            {
                if (File.Exists(tempPath))
                {   /* A partial temp file was left behind: remove it. */
                    File.Delete(tempPath);
                }
            }
            catch
            {   /* Temp file cannot be removed either: leave it; the real file is untouched. */
            }

            throw;
        }
    }

    /*------------------------------------------------------------------------*/
    /* FILE_ConfirmDiscard:                                                   */
    /*                                                                        */
    /* Called before the document is replaced or the window closes.           */
    /* Settles any changes typed into the edit panel, then, if the            */
    /* document has unsaved changes, asks whether to save them.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true to go ahead (saved, discarded, or nothing to           */
    /*            save); false if the user cancelled or a save failed.        */
    /*------------------------------------------------------------------------*/
    bool FILE_ConfirmDiscard()
    {
        DialogResult result;                             // the user's answer

        if (!EDIT_ConfirmPendingPanel())
        {   /* User cancelled at the unsaved-panel question. */
            return(false);
        }

        if (!doc.Vd_bModified)
        {   /* Nothing unsaved: go ahead. */
            return(true);
        }

        result = DLG_Message(
            "You have unsaved changes. Save before continuing?",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {   /* Save first; go ahead only if the save actually happened. */
            return(FILE_Save());
        }

        if (result == DialogResult.No)
        {   /* Throw the changes away and go ahead. */
            return(true);
        }

        /* Cancel (or the dialog closed): stay put. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* EDIT: undo, redo, and the edit panel's Save and Discard                */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* EDIT_FocusedTextBox:                                                   */
    /*                                                                        */
    /* The panel text box that has the keyboard, if any: Ctrl+Z and           */
    /* Ctrl+Y belong to it then, not to the document.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     TextBox : the focused Key, Value, Comment or Raw box, or null.     */
    /*------------------------------------------------------------------------*/
    TextBox EDIT_FocusedTextBox()
    {
        if (txtValue.Focused)
        {   /* The Value box. */
            return(txtValue);
        }

        if (txtKey.Focused)
        {   /* The Key box. */
            return(txtKey);
        }

        if (EDIT_txtComment.Focused)
        {   /* The Comment box. */
            return(EDIT_txtComment);
        }

        if (EDIT_txtRaw.Focused)
        {   /* The Raw box. */
            return(EDIT_txtRaw);
        }

        /* None: the keyboard is elsewhere. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_Undo:                                                             */
    /*                                                                        */
    /* Edit > Undo (Ctrl+Z), checked in this order:                           */
    /*     o Typing in a panel text box: undo the typing.  The menu           */
    /*       shortcut would otherwise take Ctrl+Z away from the box.          */
    /*     o An in-tree edit box is open: do nothing; it is finishing.        */
    /*     o The panel holds unsaved typed changes: those are the newest      */
    /*       change, so they are what gets undone (discarded).                */
    /*     o Otherwise the document's newest step is undone (Vd_Undo) and     */
    /*       the tree follows its changes, last first.                        */
    /* The undo history starts at every load and save, so when none is        */
    /* left the document matches the file again.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document, panel or text box steps back once.            */
    /*------------------------------------------------------------------------*/
    void EDIT_Undo()
    {
        TextBox box;                                     // a focused panel text box
        VoorheesEditStep step;                           // the step undone

        box = EDIT_FocusedTextBox();
        if (box != null)
        {   /* Typing in a box: that box's own undo. */
            box.Undo();
            return;
        }

        if (labelEditing)
        {   /* In-tree edit in progress: leave it to finish or cancel. */
            return;
        }

        if (editPanelDirty)
        {   /* Unsaved panel changes are the newest change: discard them. */
            EDIT_DiscardClick();
            return;
        }

        step = doc.Vd_Undo();
        if (step == null)
        {   /* Nothing to undo. */
            return;
        }

        DOC_ApplyStepToTree(step, true);
        DOC_AfterChange(step);
        tree.Vtv_Select(step.Ves_FocusAfterUndo());
        EDIT_LoadSelected();
    }

    /*----------------------------------------------------------------------*/
    /* EDIT_Redo:                                                           */
    /*                                                                      */
    /* Edit > Redo (Ctrl+Y): makes the most recently undone step again, and */
    /* the tree follows.  Does nothing while a text box or an in-tree edit  */
    /* has the keyboard.  Unsaved panel changes are settled first; saving   */
    /* them is a new change, which ends the redo history, so there may      */
    /* then be nothing left to redo.                                        */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the document steps forward once, if it can.               */
    /*----------------------------------------------------------------------*/
    void EDIT_Redo()
    {
        VoorheesEditStep step;                           // the step redone

        if (EDIT_FocusedTextBox() != null || labelEditing)
        {   /* Keyboard belongs to a text box or an in-tree edit. */
            return;
        }

        if (!doc.Vd_CanRedo())
        {   /* Nothing to redo. */
            return;
        }

        if (!EDIT_ConfirmPendingPanel())
        {   /* User cancelled at the unsaved-panel question. */
            return;
        }

        step = doc.Vd_Redo();
        if (step == null)
        {   /* Saving the panel was a new change and cleared the redo history. */
            return;
        }

        DOC_ApplyStepToTree(step, false);
        DOC_AfterChange(step);
        tree.Vtv_Select(step.Ves_FocusAfterDo());
        EDIT_LoadSelected();
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_DiscardClick:                                                     */
    /*                                                                        */
    /* Discard button: throws away whatever was typed into the panel and      */
    /* reloads it from the selected entry.  Also used wherever pending        */
    /* panel changes are to be dropped.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel shows the document again and is not dirty.        */
    /*------------------------------------------------------------------------*/
    void EDIT_DiscardClick()
    {
        /* Reloading refreshes every field and clears the dirty flags. */
        EDIT_LoadSelected();
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_ConfirmPendingPanel:                                              */
    /*                                                                        */
    /* If the panel holds typed changes not yet saved to the document,        */
    /* asks whether to save them: Yes saves, No discards, Cancel stops.       */
    /* Called before anything that would reload the panel and so lose         */
    /* them: selecting another entry, tree edits, undo and redo, find and     */
    /* replace, file operations, and closing the window.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true to go ahead (nothing pending, saved or                 */
    /*            discarded); false if the user cancelled or the values       */
    /*            could not be saved (bad number, used key, ...).             */
    /*------------------------------------------------------------------------*/
    bool EDIT_ConfirmPendingPanel()
    {
        DialogResult result;                             // the user's answer

        if (!editPanelDirty)
        {   /* Nothing pending. */
            return(true);
        }

        result = DLG_Message(
            "The edit panel has changes that haven't been saved to "
            + "the document.\r\n\r\nSave them now?",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {   /* Save them; go ahead only if they were valid and saved. */
            return(EDIT_Apply());
        }

        if (result == DialogResult.No)
        {   /* Drop them and go ahead. */
            EDIT_DiscardClick();
            return(true);
        }

        /* Cancel: stay put with the edits still in the panel. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_ApplyClick:                                                       */
    /*                                                                        */
    /* Save button handler.  The result does not matter here: when the        */
    /* save is refused, EDIT_Apply has already told the user why.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sender : the Save button.                                          */
    /*     e      : unused.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : see EDIT_Apply.                                             */
    /*------------------------------------------------------------------------*/
    void EDIT_ApplyClick(object sender, EventArgs e)
    {
        EDIT_Apply();
    }

    /*-----------------------------------------------------------------------*/
    /* EDIT_Apply:                                                           */
    /*                                                                       */
    /* Writes the edit panel into the selected entry, as ONE undo step that  */
    /* is all or nothing: if any part is refused, the parts already made     */
    /* are taken back (Vd_AbandonStep) and the user is told why.             */
    /*     o A Raw edit: the entry is replaced by the Raw text, parsed       */
    /*       strictly and kept exactly as typed (Vd_ActRaw).                 */
    /*     o A comment entry: its words (Vd_ActSetCommentText).              */
    /*     o A value: the new value from the Type and Value boxes            */
    /*       (EDIT_BuildValue; turning a container with items into another   */
    /*       type asks first), then a changed key (Vd_ActRename: never an    */
    /*       empty or used key), then a changed comment                      */
    /*       (Vd_ActSetLineComment).                                         */
    /* Changes are made by POSITION, so with a duplicated key only the       */
    /* selected copy changes.  Saving a panel that matches the document      */
    /* records nothing and does not mark the file modified.                  */
    /*                                                                       */
    /* Arguments:                                                            */
    /*     None.                                                             */
    /*                                                                       */
    /* Returns:                                                              */
    /*     bool : true if the panel now matches the document; false if a     */
    /*            check failed or the user declined (told why already).      */
    /*-----------------------------------------------------------------------*/
    bool EDIT_Apply()
    {
        List<int> path;                                  // the entry's path
        VoorheesItem item;                               // the entry
        VoorheesEditStep step;                           // the Save's undo step
        VoorheesNode newNode;                            // value built from the panel
        string sError;                                   // why a part was refused
        bool bDeclined;                                  // the user declined a type change

        path = TREE_SelectedPath();
        item = null;
        if (path != null)
        {   /* Something is selected: its entry. */
            item = doc.Vd_ItemAt(path);
        }

        if (item == null)
        {   /* Nothing to save into: drop the edits. */
            editPanelDirty = false;
            return(true);
        }

        step = doc.Vd_BeginStep(path);
        sError = null;

        if (EDIT_bRawDirty)
        {   /* The Raw box: the entry exactly as typed. */
            sError = doc.Vd_ActRaw(step, path, EDIT_TextFromBox(EDIT_txtRaw.Text,
                EDIT_sRawSource));
        }
        else if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment entry: its words. */
            sError = doc.Vd_ActSetCommentText(step, path,
                EDIT_TextFromBox(txtValue.Text, item.Vi_sComment));
        }
        else
        {   /* A value: value, then key, then comment. */
            newNode = EDIT_BuildValue(item, out sError, out bDeclined);
            if (bDeclined)
            {   /* The user said no to losing a container's items. */
                doc.Vd_AbandonStep(step);
                return(false);
            }

            if (sError == null)
            {   /* A valid value: set it. */
                sError = doc.Vd_ActSetValue(step, path, newNode);
            }

            if (sError == null && item.Vi_iKind == VoorheesItem.VI_MEMBER
                && txtKey.Text != item.Vi_sKey)
            {   /* The key was edited: rename (taken exactly as typed: spaces are legal in keys). */
                sError = doc.Vd_ActRename(step, path, txtKey.Text);
            }

            if (sError == null && EDIT_txtComment.Text != EDIT_sCommentShown)
            {   /* The comment was edited. */
                sError = doc.Vd_ActSetLineComment(step, path,
                    EDIT_TextFromBox(EDIT_txtComment.Text, ""));
            }
        }

        if (!DOC_Finish(step, sError))
        {   /* Refused (the user has been told why); nothing has changed. */
            return(false);
        }

        /* The panel's contents are in the document now: show it from there. */
        EDIT_LoadSelected();

        /* Saved. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_BuildValue:                                                       */
    /*                                                                        */
    /* The value the panel describes, for EDIT_Apply: the kind chosen in      */
    /* the Type box with the Value box's text or the checkbox, read by the    */
    /* format's rules (VoorheesFormat.Vf_ValueFromPanel: for JSON a number    */
    /* must read as one, a container already of the chosen kind is kept       */
    /* with its items, a string keeps its line endings).  Two things are      */
    /* the editor's own:                                                      */
    /*     o Comment : refused: a value cannot become a comment;              */
    /*     o turning a container WITH items into anything else deletes        */
    /*       them, so the user is asked first.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item      : the entry being edited (a value).                      */
    /*     sError    : set to why the panel's value is refused, else null.    */
    /*     bDeclined : set to true when the user declined to lose items.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null when refused or declined.        */
    /*------------------------------------------------------------------------*/
    VoorheesNode EDIT_BuildValue(VoorheesItem item, out string sError,
        out bool bDeclined)
    {
        VoorheesNode oldNode;                            // the entry's value now
        VoorheesNode newNode;                            // the value built
        int iKind;                                       // the Type box's choice

        sError = null;
        bDeclined = false;
        oldNode = item.Vi_node;
        iKind = EDIT_SelectedKind();

        if (iKind == EDIT_KINDCOMMENT)
        {   /* A value cannot be turned into a comment. */
            sError = "A value can't become a comment.  To add a comment, "
                + "use Add Child > Comment on its object or array.";
            return(null);
        }

        /* The format reads the panel. */
        newNode = VoorheesFormat.Vf_ValueFromPanel(doc, iKind, txtValue.Text,
            chkBoolValue.Checked, oldNode, out sError);

        if (newNode != null && oldNode.Vn_IsContainer()
            && !ReferenceEquals(newNode, oldNode) && oldNode.Vn_Count() > 0)
        {   /* A container with items is becoming another type, which deletes them: ask first. */
            if (DLG_Message("Changing this "
                + VoorheesFormat.Vf_KindName(doc.Vd_iFormat, oldNode.Vn_iKind).ToLower() + " to "
                + cboType.Text.ToLower() + " will delete its "
                + oldNode.Vn_Count().ToString() + " item(s).\r\n\r\n"
                + "Continue?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            {   /* Declined: change nothing. */
                bDeclined = true;
                return(null);
            }
        }

        /* The value, or null with the reason. */
        return(newNode);
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_TextForBox:                                                       */
    /*                                                                        */
    /* Prepares text for a multi-line box.  The Windows text box only         */
    /* breaks lines at CRLF, so text holding bare LFs would show on one       */
    /* line; every line break becomes CRLF for display.                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     text : the text as stored.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the same text with every line break as CRLF.              */
    /*------------------------------------------------------------------------*/
    string EDIT_TextForBox(string text)
    {
        /* CRLF to LF first, so existing CRLFs are not doubled. */
        return(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_TextFromBox:                                                      */
    /*                                                                        */
    /* Text from a box (which uses CRLF) back in the line endings of the      */
    /* text it came from: CRLF if that used CRLF, LF if it used LF, and       */
    /* otherwise the document's own line ending.  So an edited entry          */
    /* keeps the file's line endings.                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     text   : the box's text.                                           */
    /*     sample : the text the box was loaded from ("" if none).            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text with fitting line breaks.                        */
    /*------------------------------------------------------------------------*/
    string EDIT_TextFromBox(string text, string sample)
    {
        string sNewline;                                 // the line ending to use

        if (sample.IndexOf("\r\n", StringComparison.Ordinal) >= 0)
        {   /* The original used CRLF. */
            sNewline = "\r\n";
        }
        else if (sample.IndexOf('\n') >= 0)
        {   /* The original used LF. */
            sNewline = "\n";
        }
        else
        {   /* No line breaks to go by: the document's own. */
            sNewline = doc.Vd_sNewline;
        }

        /* Every box line break as the chosen ending. */
        return(text.Replace("\r\n", "\n").Replace("\n", sNewline));
    }

    /*----------------------------------------------------------------------*/
    /* EDIT_UpdateValueEditor:                                              */
    /*                                                                      */
    /* Shows the right value editor for the kind chosen in the Type box:    */
    /* the checkbox for a boolean kind (Vf_IsBooleanKind), the Value box    */
    /* for everything else.  When the user switches TO a boolean the        */
    /* checkbox starts out matching the Value box ("true" ticks it).  The   */
    /* Value box is greyed out for kinds with no value to type              */
    /* (Vf_KindHasText: JSON null, object and array).                       */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : txtValue and chkBoolValue are shown, hidden, enabled or   */
    /*            disabled.                                                 */
    /*----------------------------------------------------------------------*/
    void EDIT_UpdateValueEditor()
    {
        int iKind;                                       // kind chosen in the Type box
        string current;                                  // Value box text, for the checkbox

        iKind = EDIT_SelectedKind();

        if (iKind != EDIT_KINDCOMMENT
            && VoorheesFormat.Vf_IsBooleanKind(doc.Vd_iFormat, iKind))
        {   /* Boolean: show the checkbox in place of the Value box. */
            txtValue.Visible = false;
            chkBoolValue.Visible = true;

            /* Start the checkbox from the Value box's text. */
            current = txtValue.Text.Trim().ToLower();
            chkBoolValue.Checked = (current == "true");
        }
        else
        {   /* Any other type: show the Value box. */
            chkBoolValue.Visible = false;
            txtValue.Visible = true;
        }

        if (iKind == EDIT_KINDCOMMENT)
        {   /* A comment entry: its words are typed in the Value box. */
            txtValue.Enabled = true;
        }
        else
        {   /* A value: typed only if its kind has text (not JSON null, object, array). */
            txtValue.Enabled = VoorheesFormat.Vf_KindHasText(doc.Vd_iFormat,
                iKind);
        }
    }

    /*----------------------------------------------------------------------*/
    /* EDIT_FillTypeBox:                                                    */
    /*                                                                      */
    /* Gives the Type box the value kinds for an entry, each named by its   */
    /* format (Vf_KindName), followed by Comment.  Left alone when the      */
    /* kinds are the ones it already holds, so moving between entries of    */
    /* one format does not rebuild it; when they change, the panel is laid  */
    /* out again, since the box is sized to its longest entry.              */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     lstKinds : the kinds, in order (from Vf_PanelKinds).             */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : cboType and EDIT_lstTypeKinds hold the kinds; nothing is  */
    /*            selected when they changed.                               */
    /*----------------------------------------------------------------------*/
    void EDIT_FillTypeBox(List<int> lstKinds)
    {
        bool bSame;                                      // the box already holds these kinds
        int i;

        /* The same kinds in the same order: nothing to do. */
        bSame = (lstKinds.Count == EDIT_lstTypeKinds.Count);
        for (i = 0; bSame && i < lstKinds.Count; i++)
        {
            bSame = (lstKinds[i] == EDIT_lstTypeKinds[i]);
        }
        if (bSame)
        {   /* Already filled with them. */
            return;
        }

        /* Rebuild: each kind by its format's name, then Comment. */
        EDIT_lstTypeKinds = new List<int>(lstKinds);
        cboType.Items.Clear();
        for (i = 0; i < lstKinds.Count; i++)
        {
            cboType.Items.Add(VoorheesFormat.Vf_KindName(doc.Vd_iFormat,
                lstKinds[i]));
        }
        cboType.Items.Add("Comment");

        /* The box's width follows its entries. */
        EDIT_LayoutPanel();
    }

    /*----------------------------------------------------------------------*/
    /* EDIT_SelectedKind:                                                   */
    /*                                                                      */
    /* The kind chosen in the Type box.                                     */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     int : the kind behind the chosen entry, or EDIT_KINDCOMMENT for  */
    /*           the Comment entry (and when nothing is chosen).            */
    /*----------------------------------------------------------------------*/
    int EDIT_SelectedKind()
    {
        int iIndex;                                      // the chosen entry

        iIndex = cboType.SelectedIndex;
        if (iIndex >= 0 && iIndex < EDIT_lstTypeKinds.Count)
        {   /* A value kind's entry. */
            return(EDIT_lstTypeKinds[iIndex]);
        }

        /* The last entry, Comment (or none). */
        return(EDIT_KINDCOMMENT);
    }

    /*----------------------------------------------------------------------*/
    /* EDIT_SelectKind:                                                     */
    /*                                                                      */
    /* Chooses a kind's entry in the Type box.                              */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     iKind : a kind the box holds, or EDIT_KINDCOMMENT.               */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : cboType's selection changes (its handler runs); a kind    */
    /*            the box does not hold selects nothing.                    */
    /*----------------------------------------------------------------------*/
    void EDIT_SelectKind(int iKind)
    {
        if (iKind == EDIT_KINDCOMMENT)
        {   /* Comment: always the last entry. */
            cboType.SelectedIndex = cboType.Items.Count - 1;
            return;
        }

        /* A value kind: its entry, or -1 (nothing) if it is not offered. */
        cboType.SelectedIndex = EDIT_lstTypeKinds.IndexOf(iKind);
    }

    /*----------------------------------------------------------------------*/
    /* EDIT_LoadSelected:                                                   */
    /*                                                                      */
    /* Loads the selected entry into the panel (EDIT_LoadPanel), or clears  */
    /* the dirty flags when nothing is selected.                            */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the panel shows the selected entry.                       */
    /*----------------------------------------------------------------------*/
    void EDIT_LoadSelected()
    {
        if (tree.SelectedNode == null || tree.SelectedNode.Tag == null)
        {   /* Nothing selected (an empty document): an empty, locked panel; forget any edits. */
            EDIT_ClearPanel();
            editPanelDirty = false;
            EDIT_bRawDirty = false;
            return;
        }

        EDIT_LoadPanel(tree.SelectedNode);
    }

    /*----------------------------------------------------------------------*/
    /* EDIT_ClearPanel:                                                     */
    /*                                                                      */
    /* Empties the edit panel when no entry is selected (an empty document, */
    /* such as a new INI file), so it never shows an entry of a document    */
    /* that is gone: every box empty and read-only, the Type box without a  */
    /* choice and disabled.  EDIT_LoadPanel unlocks everything again for    */
    /* the next entry selected.                                             */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the panel is empty and not dirty.                         */
    /*----------------------------------------------------------------------*/
    void EDIT_ClearPanel()
    {
        /* Filling the panel from code: not an edit. */
        suppressPanelDirty = true;

        txtKey.Text = "";
        txtKey.Enabled = false;
        cboType.SelectedIndex = -1;
        cboType.Enabled = false;
        txtValue.Text = "";
        txtValue.ReadOnly = true;
        chkBoolValue.Visible = false;
        txtValue.Visible = true;
        EDIT_txtComment.Text = "";
        EDIT_txtComment.ReadOnly = true;
        EDIT_txtRaw.Text = "";
        EDIT_txtRaw.ReadOnly = true;
        EDIT_sCommentShown = "";
        EDIT_sRawSource = "";
        EDIT_sRawShown = "";
        EDIT_bRawTruncated = false;

        /* Done: nothing to edit until an entry is selected. */
        suppressPanelDirty = false;
    }

    /*------------------------------------------------------------------------*/
    /* EDIT_LoadPanel:                                                        */
    /*                                                                        */
    /* Loads an entry into the edit panel, with suppressPanelDirty raised     */
    /* so loading does not count as editing, and every field unlocked:        */
    /*     o Key     : an object member's key (editable); an array            */
    /*                 element's index, or nothing, otherwise (read-only);    */
    /*     o Type    : the value's kind; Comment (fixed) for a comment        */
    /*                 entry;                                                 */
    /*     o Value   : a plain value's text (line breaks as CRLF), a          */
    /*                 comment entry's words (read-only when it holds         */
    /*                 several comments), nothing for a container;            */
    /*     o Comment : the comment on the entry's line, without markers       */
    /*                 (read-only when it holds several); hidden for a        */
    /*                 comment entry;                                         */
    /*     o Raw     : the entry's text exactly as in the file -- the start   */
    /*                 of it, read-only, when it is longer than EDIT_RAWMAX.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode : the entry's tree node.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the panel shows the entry and is not dirty.                 */
    /*------------------------------------------------------------------------*/
    void EDIT_LoadPanel(TreeNode treeNode)
    {
        List<int> path;                                  // the entry's path
        VoorheesItem item;                               // the entry
        VoorheesNode container;                          // its container
        string sLineComment;                             // the comment on its line
        string sRaw;                                     // its raw text
        bool bTruncated;                                 // the raw text was cut
        bool bComment;                                   // it is a comment entry
        bool bLineComment;                               // its format gives it a line comment (Comment box)

        path = VoorheesTreeView.Vtv_PathOf(treeNode);
        item = doc.Vd_ItemAt(path);
        if (item == null)
        {   /* Tree out of step with the document: show nothing new. */
            return;
        }
        container = doc.Vd_NodeAt(path.GetRange(0, path.Count - 1));
        bComment = (item.Vi_iKind == VoorheesItem.VI_COMMENT);

        /* Filling the panel from code: not an edit. */
        suppressPanelDirty = true;

        /* Every field unlocked; Raw and the others lock each other   */
        /* again on the first edit (EDIT_MarkDirty).                  */
        txtKey.ReadOnly = false;
        cboType.Enabled = !bComment;
        txtValue.ReadOnly = false;
        chkBoolValue.Enabled = true;
        EDIT_txtComment.ReadOnly = false;

        /* Key: a member's key; an element's index; nothing otherwise. */
        if (item.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* An object member: its key, editable. */
            txtKey.Text = item.Vi_sKey;
            txtKey.Enabled = true;
        }
        else if (!bComment && item.Vi_iKind == VoorheesItem.VI_ELEMENT
            && container.Vn_iKind != VoorheesNode.VN_DOCUMENT)
        {   /* An array element: its index, for information. */
            txtKey.Text = container.Vn_ElementIndex(path[path.Count - 1])
                .ToString(CultureInfo.InvariantCulture);
            txtKey.Enabled = false;
        }
        else
        {   /* The top value or a comment: no key. */
            txtKey.Text = "";
            txtKey.Enabled = false;
        }

        if (bComment)
        {   /* A comment entry: its words in the Value box. */
            EDIT_SelectKind(EDIT_KINDCOMMENT);
            txtValue.Text = EDIT_TextForBox(VoorheesFormat.Vf_CommentText(doc.Vd_iFormat, 
                item.Vi_sComment));
            txtValue.ReadOnly = !VoorheesFormat.Vf_IsSingleComment(doc.Vd_iFormat, item.Vi_sComment);
            EDIT_lblComment.Visible = false;
            EDIT_txtComment.Visible = false;
            EDIT_txtComment.Text = "";
        }
        else
        {   /* A value: the kinds its format offers there, its own chosen. */
            EDIT_FillTypeBox(VoorheesFormat.Vf_PanelKinds(doc, path));
            EDIT_SelectKind(item.Vi_node.Vn_iKind);
            if (item.Vi_node.Vn_IsContainer())
            {   /* A container: no typed value. */
                txtValue.Text = "";
            }
            else
            {   /* Plain value: show it. */
                txtValue.Text = EDIT_TextForBox(item.Vi_node.Vn_DisplayValue());
            }

            if (VoorheesFormat.Vf_IsBooleanKind(doc.Vd_iFormat,
                item.Vi_node.Vn_iKind))
            {   /* Boolean: the checkbox takes the value. */
                chkBoolValue.Checked = (item.Vi_node.Vn_sText == "true");
            }

            /* The comment on its line, where its format has one. */
            sLineComment = item.Vi_LineComment();
            bLineComment = VoorheesFormat.Vf_HasLineComment(doc, container,
                item);
            EDIT_lblComment.Visible = bLineComment;
            EDIT_txtComment.Visible = bLineComment;
            EDIT_txtComment.Text = "";
            if (bLineComment)
            {   /* Shown: its words, on one line; read-only when it holds several comments. */
                EDIT_txtComment.Text = VoorheesFormat.Vf_CommentText(
                    doc.Vd_iFormat, sLineComment)
                    .Replace("\r\n", " ").Replace("\n", " ");
                EDIT_txtComment.ReadOnly = (sLineComment != null
                    && !VoorheesFormat.Vf_IsSingleComment(doc.Vd_iFormat,
                    sLineComment));
            }
        }
        EDIT_sCommentShown = EDIT_txtComment.Text;

        /* Raw: the entry as in the file, cut and read-only when huge. */
        sRaw = VoorheesFormat.Vf_RenderItemLimited(doc, path, EDIT_RAWMAX,
            out bTruncated);
        EDIT_sRawSource = sRaw;
        EDIT_bRawTruncated = bTruncated;
        if (bTruncated)
        {   /* Too long to edit here: its start, and why it stops. */
            EDIT_txtRaw.Text = EDIT_TextForBox(sRaw) + "\r\n\r\n... (more than "
                + (EDIT_RAWMAX / 1024).ToString(CultureInfo.InvariantCulture)
                + " KB: too large to edit as raw text; edit its entries instead)";
            EDIT_txtRaw.ReadOnly = true;
        }
        else
        {   /* All of it, editable. */
            EDIT_txtRaw.Text = EDIT_TextForBox(sRaw);
            EDIT_txtRaw.ReadOnly = false;
        }
        EDIT_sRawShown = EDIT_txtRaw.Text;

        /* Show the editor that suits the type. */
        EDIT_UpdateValueEditor();

        /* Done: the panel now matches the document. */
        suppressPanelDirty = false;
        editPanelDirty = false;
        EDIT_bRawDirty = false;
    }

    /*------------------------------------------------------------------------*/
    /* DOC: finishing edits, and replacing the document                       */
    /*------------------------------------------------------------------------*/

    /*----------------------------------------------------------------------*/
    /* DOC_SetDocument:                                                     */
    /*                                                                      */
    /* Makes a document the one being edited: New, Open and Close.  The     */
    /* tree shows its top level with the top value open and selected (which */
    /* loads the panel), and the title and status bar follow.               */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     newDoc : the document.                                           */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the document, tree, panel and title are all replaced.     */
    /*----------------------------------------------------------------------*/
    void DOC_SetDocument(VoorheesDocument newDoc)
    {
        Stopwatch swTime;                                // times the tree for the debug log

        swTime = Stopwatch.StartNew();
        doc = newDoc;
        editPanelDirty = false;
        EDIT_bRawDirty = false;
        DOC_bHasComments = doc.Vd_HasComments();
        DOC_bEditWarned = false;

        tree.Vtv_SetDocument(doc);
        EDIT_LoadSelected();
        STATE_UpdateTitle();

        Program.APP_DebugLog("tree shown in "
            + swTime.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
            + " ms");
    }

    /*----------------------------------------------------------------------*/
    /* DOC_Finish:                                                          */
    /*                                                                      */
    /* Finishes every edit.  A refused action (sError set) has its step     */
    /* taken back -- for a Save of several parts, the parts already made -- */
    /* and the user is told why.  Otherwise the step is ended (Vd_EndStep): */
    /* if it changed anything it becomes an undo step, its changes are      */
    /* applied to the tree, the comment check and title follow, and the     */
    /* step's focus is selected.                                            */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     step   : the step, with any focus set.                           */
    /*     sError : why the action was refused, or null.                    */
    /*                                                                      */
    /* Returns:                                                             */
    /*     bool : false when refused; true otherwise (changed or not).      */
    /*----------------------------------------------------------------------*/
    bool DOC_Finish(VoorheesEditStep step, string sError)
    {
        Stopwatch swTime;                                // times the tree update for the debug log

        if (sError != null)
        {   /* Refused: take back whatever was made, and say why. */
            doc.Vd_AbandonStep(step);
            DLG_Message(sError, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return(false);
        }

        if (!doc.Vd_EndStep(step))
        {   /* Nothing changed: nothing to show. */
            return(true);
        }

        swTime = Stopwatch.StartNew();
        DOC_ApplyStepToTree(step, false);
        DOC_AfterChange(step);
        tree.Vtv_Select(step.Ves_FocusAfterDo());
        Program.APP_DebugLog("edit of "
            + step.Ves_lstChanges.Count.ToString(CultureInfo.InvariantCulture)
            + " change(s) shown in "
            + swTime.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
            + " ms");

        /* Done. */
        return(true);
    }

    /*----------------------------------------------------------------------*/
    /* DOC_ApplyStepToTree:                                                 */
    /*                                                                      */
    /* Applies a step's changes to the tree, in the order the document made */
    /* them: first to last when done or redone, last to first when undone.  */
    /* A step of many changes (Replace All) counts them on the progress     */
    /* window, which appears if the work outlasts PROG_SHOWDELAYMS.         */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     step     : the step.                                             */
    /*     bReverse : it was undone.                                        */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the tree matches the document.                            */
    /*----------------------------------------------------------------------*/
    void DOC_ApplyStepToTree(VoorheesEditStep step, bool bReverse)
    {
        BusyModalForm prog;                              // progress window, shown only if this is slow
        int iCount;                                      // changes in the step
        int i;

        iCount = step.Ves_lstChanges.Count;
        if (iCount < DOC_PROGRESSSTEP)
        {   /* A few changes (every ordinary edit): no progress window needed. */
            for (i = 0; i < iCount; i++)
            {
                if (bReverse)
                {   /* Undone: last change first. */
                    tree.Vtv_ApplyChange(step.Ves_lstChanges[iCount - 1 - i], true);
                }
                else
                {   /* Done or redone: first change first. */
                    tree.Vtv_ApplyChange(step.Ves_lstChanges[i], false);
                }
            }
            return;
        }

        /* Many changes (Replace All): counted, the window shown if slow. */
        prog = BusyModalForm.BMF_BeginUiProgress(this, "Updating the tree",
            DOC_Name(), "Voorhees", PROG_SHOWDELAYMS);
        try
        {
            for (i = 0; i < iCount; i++)
            {
                if (bReverse)
                {   /* Undone: last change first. */
                    tree.Vtv_ApplyChange(step.Ves_lstChanges[iCount - 1 - i], true);
                }
                else
                {   /* Done or redone: first change first. */
                    tree.Vtv_ApplyChange(step.Ves_lstChanges[i], false);
                }

                if (i % DOC_PROGRESSSTEP == 0)
                {   /* Now and then: let the window appear or move. */
                    prog.BMF_ReportUiProgress(i, iCount);
                }
            }
        }
        finally
        {
            prog.BMF_EndUiProgress();
        }
    }

    /*----------------------------------------------------------------------*/
    /* Changes applied to the tree between progress reports.                */
    /*----------------------------------------------------------------------*/

    const int DOC_PROGRESSSTEP = 500;                    // tree changes per BMF_ReportUiProgress call

    /*------------------------------------------------------------------------*/
    /* DOC_AfterChange:                                                       */
    /*                                                                        */
    /* After every change, undo or redo: notes whether the document now       */
    /* holds comments, and shows the format's warning about the change, if    */
    /* it has one, at most once per document (Vf_EditWarning: for JSON the    */
    /* first comment in a document that had none makes it JSONC, which        */
    /* many programs refuse; undo takes the comment away again).  Then the    */
    /* title and status bar.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step : the step just made, undone or redone.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : DOC_bHasComments is current and the display updated.        */
    /*------------------------------------------------------------------------*/
    void DOC_AfterChange(VoorheesEditStep step)
    {
        bool bHadComments;                               // the document had comments before
        string sWarning;                                 // the format's warning, if any

        bHadComments = DOC_bHasComments;
        DOC_bHasComments = doc.Vd_HasComments();
        if (!DOC_bEditWarned)
        {   /* Not warned yet for this document: does the format have anything to say? */
            sWarning = VoorheesFormat.Vf_EditWarning(doc, step, bHadComments,
                DOC_bHasComments);
            if (sWarning != null)
            {   /* It does: say it, once. */
                DOC_bEditWarned = true;
                DLG_Message(sWarning, MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        STATE_UpdateTitle();
    }

    /*----------------------------------------------------------------------*/
    /* DOC_Name:                                                            */
    /*                                                                      */
    /* The document's file name, for progress windows.                      */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     string : the name, or "Untitled".                                */
    /*----------------------------------------------------------------------*/
    string DOC_Name()
    {
        if (doc == null || doc.Vd_sPath == null)
        {   /* Never saved. */
            return("Untitled");
        }

        /* The file's name without its folder. */
        return(Path.GetFileName(doc.Vd_sPath));
    }

    /*------------------------------------------------------------------------*/
    /* PROG: progress shown during long work                                  */
    /*------------------------------------------------------------------------*/

    /*----------------------------------------------------------------------*/
    /* How long work may run before the progress dialog appears,            */
    /* milliseconds.  Shorter work never shows it, so opening or saving     */
    /* an ordinary file does not flash a window; anything longer is no      */
    /* longer "a moment" and gets the dialog.                               */
    /*----------------------------------------------------------------------*/

    const int PROG_SHOWDELAYMS = 300;                    // delay before BusyModalForm appears

    /*----------------------------------------------------------------------*/
    /* PROG_Run:                                                            */
    /*                                                                      */
    /* Runs work on a worker thread behind the progress dialog, which       */
    /* appears only if the work outlasts PROG_SHOWDELAYMS, and returns      */
    /* when the work has finished.  For document work that can be slow on   */
    /* a large file but should never fail: searching, Replace All.  The     */
    /* work must not touch any control; it may read and change the          */
    /* document, because the dialog (or the wait before it) keeps the user  */
    /* from doing anything else until it is done.  An exception from the    */
    /* work is passed on to the caller, wrapped so its original stack is    */
    /* kept as the inner exception.                                         */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sTitle : what is happening, for the dialog's first line.         */
    /*     work   : the work.                                               */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the work has run; a failure is thrown.                    */
    /*----------------------------------------------------------------------*/
    void PROG_Run(string sTitle, Action work)
    {
        Exception exWork;                                // what the work threw

        exWork = BusyModalForm.Run(this, sTitle, DOC_Name(), work, "Voorhees",
            PROG_SHOWDELAYMS);
        if (exWork != null)
        {   /* The work failed, which it is not expected to: pass it on. */
            throw new InvalidOperationException(sTitle + ": " + exWork.Message,
                exWork);
        }
    }

    /*------------------------------------------------------------------------*/
    /* PROG_Describe:                                                         */
    /*                                                                        */
    /* The message for an exception that came back from work run behind       */
    /* the progress dialog.  The worker's own expected failures (an           */
    /* unreadable file, say) are reported by the work itself; this covers     */
    /* the rest, including the case where no exception was returned but       */
    /* the work produced nothing.                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     ex : the exception from BusyModalForm.Run, or null.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text to show the user.                                */
    /*------------------------------------------------------------------------*/
    static string PROG_Describe(Exception ex)
    {
        if (ex == null)
        {   /* The work ended without a result or an exception. */
            return("The operation did not complete.");
        }

        /* The exception's own explanation. */
        return(ex.Message);
    }

    /*------------------------------------------------------------------------*/
    /* STATE: title bar and status strip                                      */
    /*------------------------------------------------------------------------*/

    /*----------------------------------------------------------------------*/
    /* STATE_UpdateTitle:                                                   */
    /*                                                                      */
    /* Shows the open file in the title bar ("Voorhees - name.json", with   */
    /* " *" while modified, or "Untitled") and, in the status strip, its    */
    /* full path, whether it contains comments, and its modified state.     */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : Text and the status labels are updated.                   */
    /*----------------------------------------------------------------------*/
    void STATE_UpdateTitle()
    {
        string fileName;                                 // name shown in the title bar
        string mod;                                      // " *" while modified

        if (doc.Vd_sPath != null)
        {   /* A file is open: show its name in the title and path in the status bar. */
            fileName = Path.GetFileName(doc.Vd_sPath);
            lblFilePath.Text = doc.Vd_sPath;
        }
        else
        {   /* Never saved. */
            fileName = "Untitled";
            lblFilePath.Text = "(no file)";
        }

        /* The format, by name. */
        STATE_lblFormat.Text = VoorheesFormat.Vf_Name(doc.Vd_iFormat);

        if (DOC_bHasComments
            && VoorheesFormat.Vf_CommentsMakeNonStandard(doc.Vd_iFormat))
        {   /* Comments in a format whose standard has none (JSON): say so. */
            STATE_lblComments.Text = "Contains comments";
        }
        else
        {   /* Standard for its format. */
            STATE_lblComments.Text = "";
        }

        if (doc.Vd_bModified)
        {   /* Unsaved changes. */
            mod = " *";
            lblModified.Text = "Modified";
        }
        else
        {   /* Matches the file on disk. */
            mod = "";
            lblModified.Text = "";
        }

        Text = "Voorhees - " + fileName + mod;
    }

    /*------------------------------------------------------------------------*/
    /* TREE: selection and in-place editing in the tree                       */
    /*------------------------------------------------------------------------*/

    /*----------------------------------------------------------------------*/
    /* TREE_SelectedPath:                                                   */
    /*                                                                      */
    /* The position path of the selected entry.                             */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     List<int> : the path, or null when nothing is selected.          */
    /*----------------------------------------------------------------------*/
    List<int> TREE_SelectedPath()
    {
        if (tree.SelectedNode == null || tree.SelectedNode.Tag == null)
        {   /* Nothing selected. */
            return(null);
        }

        /* Its indexes, top first. */
        return(VoorheesTreeView.Vtv_PathOf(tree.SelectedNode));
    }

    /*----------------------------------------------------------------------*/
    /* TREE_BeforeSelect:                                                   */
    /*                                                                      */
    /* Guards the edit panel.  A click or arrow key that moves the          */
    /* selection would reload the panel and lose anything typed there.      */
    /* When there is such typing, the move is held, the user is asked,      */
    /* and the move is finished once the question is answered.  The         */
    /* question runs after this event returns, because saving the panel     */
    /* changes the tree, which must not happen inside a selection event.    */
    /* Selections made by code (Action Unknown) and by the tree's own       */
    /* rearranging are never held.                                          */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sender : the tree.                                               */
    /*     e      : the node about to be selected; e.Cancel holds it.       */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the selection goes ahead, or is held and redone later.    */
    /*----------------------------------------------------------------------*/
    void TREE_BeforeSelect(object sender, TreeViewCancelEventArgs e)
    {
        List<int> target;                                // path of the entry the user picked

        if (tree.Vtv_IsBusy() || !editPanelDirty)
        {   /* The tree rearranging itself, or nothing typed in the panel: let it through. */
            return;
        }

        if (e.Action == TreeViewAction.Unknown)
        {   /* Selected by code, not by the user: let it through. */
            return;
        }

        /* Hold the move, then ask once this event is over. */
        target = VoorheesTreeView.Vtv_PathOf(e.Node);
        e.Cancel = true;

        BeginInvoke((MethodInvoker)delegate
        {
            if (EDIT_ConfirmPendingPanel())
            {   /* Saved or discarded: finish the move the user asked for. */
                tree.Vtv_Select(target);
            }
        });
    }

    /*----------------------------------------------------------------------*/
    /* TREE_AfterSelect:                                                    */
    /*                                                                      */
    /* Loads the newly selected entry into the edit panel.  Ignored while   */
    /* the tree is rearranging itself; the editor reloads the panel itself  */
    /* when it has finished a change.                                       */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sender : the tree.                                               */
    /*     e      : the node just selected.                                 */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the panel shows the entry.                                */
    /*----------------------------------------------------------------------*/
    void TREE_AfterSelect(object sender, TreeViewEventArgs e)
    {
        if (tree.Vtv_IsBusy() || e.Node == null || e.Node.Tag == null)
        {   /* The tree rearranging itself, or no real entry: leave the panel as it is. */
            return;
        }

        EDIT_LoadPanel(e.Node);
    }

    /*----------------------------------------------------------------------*/
    /* TREE_NodeMouseDoubleClick:                                           */
    /*                                                                      */
    /* A left double click on a plain value or a comment edits it in        */
    /* place.  (A double click on an object or array just opens or closes   */
    /* it, as usual.)                                                       */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sender : the tree.                                               */
    /*     e      : the node clicked and the mouse button.                  */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : see TREE_BeginInlineEdit.                                 */
    /*----------------------------------------------------------------------*/
    void TREE_NodeMouseDoubleClick(object sender,
        TreeNodeMouseClickEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {   /* Left double click: edit in place. */
            TREE_BeginInlineEdit(e.Node);
        }
    }

    /*------------------------------------------------------------------------*/
    /* TREE_KeyDown:                                                          */
    /*                                                                        */
    /* F2 edits the selected value or comment in place, as in                 */
    /* Explorer.  Marking the key handled stops the tree starting its         */
    /* own label edit as well.                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sender : the tree.                                                 */
    /*     e      : the key pressed.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : see TREE_BeginInlineEdit.                                   */
    /*------------------------------------------------------------------------*/
    void TREE_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2 && e.Modifiers == Keys.None)
        {   /* Plain F2: edit in place. */
            TREE_BeginInlineEdit(tree.SelectedNode);
            e.Handled = true;
        }
    }

    /*------------------------------------------------------------------------*/
    /* TREE_BeginInlineEdit:                                                  */
    /*                                                                        */
    /* Starts editing a plain value, or a comment entry's words, inside       */
    /* the tree.  The edit box holds the FULL value (the label's value is     */
    /* cut to VTV_LEAFMAX characters, and committing that would write the     */
    /* "..." into the data) and opens to the right of the key, which stays    */
    /* in view: "key: [value]" (Vtv_PrepareValueEdit).  Text with line        */
    /* breaks goes to the Value box instead, because the in-tree edit box is  */
    /* a single line and would lose them.  Unsaved panel changes are settled  */
    /* first.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode : the node to edit.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : an edit box opens over the node, the Value box gets         */
    /*            the focus, or nothing happens (a container).                */
    /*------------------------------------------------------------------------*/
    void TREE_BeginInlineEdit(TreeNode treeNode)
    {
        VoorheesItem item;                               // the entry
        List<int> path;                                  // its path
        string value;                                    // full text to edit

        if (treeNode == null || treeNode.Tag == null)
        {   /* Nothing to edit. */
            return;
        }

        if (treeNode != tree.SelectedNode)
        {   /* Double click on another node with typed panel changes: TREE_BeforeSelect is already asking. */
            return;
        }

        path = VoorheesTreeView.Vtv_PathOf(treeNode);
        if (editPanelDirty)
        {   /* Typed changes for this entry in the panel: settle them, then find the node again. */
            if (!EDIT_ConfirmPendingPanel())
            {   /* Cancelled: no in-place edit. */
                return;
            }

            /* Saving may have replaced the node. */
            treeNode = tree.Vtv_NodeAt(path, false, false);
            if (treeNode == null)
            {   /* No longer there. */
                return;
            }
        }

        item = doc.Vd_ItemAt(path);
        if (item == null)
        {   /* Tree out of step with the document. */
            return;
        }

        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment entry: its words (several comments: Raw only). */
            if (!VoorheesFormat.Vf_IsSingleComment(doc.Vd_iFormat, item.Vi_sComment))
            {   /* Not editable here. */
                return;
            }
            value = VoorheesFormat.Vf_CommentText(doc.Vd_iFormat, item.Vi_sComment);
        }
        else if (item.Vi_node.Vn_IsContainer())
        {   /* Object or array: no single value to edit. */
            return;
        }
        else
        {   /* A plain value. */
            value = item.Vi_node.Vn_DisplayValue();
        }

        if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
        {   /* Multi-line text: edit it in the Value box, which keeps its line breaks. */
            txtValue.Focus();
            txtValue.SelectAll();
            return;
        }

        /* Edit the full text, after the key.  inlineEditRequested is   */
        /* what lets TREE_BeforeLabelEdit allow this edit; BeginEdit    */
        /* raises that event before it returns.                         */
        tree.Vtv_PrepareValueEdit(treeNode, value);
        inlineEditRequested = true;
        treeNode.BeginEdit();
        inlineEditRequested = false;

        if (!labelEditing)
        {   /* The edit box did not open: forget the edit and put the normal label back. */
            tree.Vtv_EndValueEdit();
            tree.Vtv_RefreshLabel(treeNode);
        }
    }

    /*----------------------------------------------------------------------*/
    /* TREE_BeforeLabelEdit:                                                */
    /*                                                                      */
    /* With LabelEdit on, Windows also opens an edit box on ANY node from   */
    /* a slow second click; finishing such an edit once replaced a whole    */
    /* object or array with a string.  Only edits started by                */
    /* TREE_BeginInlineEdit are let through.                                */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sender : the tree.                                               */
    /*     e      : the edit about to start; e.CancelEdit stops it.         */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the edit opens or is cancelled.                           */
    /*----------------------------------------------------------------------*/
    void TREE_BeforeLabelEdit(object sender, NodeLabelEditEventArgs e)
    {
        if (!inlineEditRequested)
        {   /* Started by Windows (slow click), not by Voorhees: refuse it. */
            e.CancelEdit = true;
            return;
        }

        /* Ours: note that an edit box is open (EDIT_Undo checks this). */
        labelEditing = true;
    }

    /*----------------------------------------------------------------------*/
    /* TREE_AfterLabelEdit:                                                 */
    /*                                                                      */
    /* The in-place edit box closed.  The typed text is handed to           */
    /* TREE_CommitInlineEdit once this event is over: the commit changes    */
    /* the tree, and doing that inside the event would disturb the node     */
    /* Windows is still finishing the edit on.  The edit itself is always   */
    /* cancelled, because the label is redrawn from the document anyway.    */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sender : the tree.                                               */
    /*     e      : the node and the typed text (null if cancelled).        */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the commit is queued.                                     */
    /*----------------------------------------------------------------------*/
    void TREE_AfterLabelEdit(object sender, NodeLabelEditEventArgs e)
    {
        List<int> path;                                  // the edited entry's path
        string newText;                                  // what was typed; null if cancelled

        labelEditing = false;
        path = VoorheesTreeView.Vtv_PathOf(e.Node);
        newText = e.Label;

        /* The label is regenerated from the document, never kept. */
        e.CancelEdit = true;

        BeginInvoke((MethodInvoker)delegate
        {
            TREE_CommitInlineEdit(path, newText);
        });
    }

    /*------------------------------------------------------------------------*/
    /* TREE_CommitInlineEdit:                                                 */
    /*                                                                        */
    /* Applies an in-place edit.  A comment entry gets the new words          */
    /* (Vd_ActSetCommentText).  A value keeps its TYPE (Vjs_ConvertText): a   */
    /* string stays a string even if the text looks like a number ("007"),    */
    /* and text that is not a valid number or boolean is refused for those    */
    /* types.  Only a null can become whatever the text looks like,           */
    /* because it has no type to keep.  (Changing a value's type is what      */
    /* the panel's Type box is for.)  The node's normal label comes back      */
    /* whatever happens.                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     path : path of the edited entry.                                   */
    /*     text : the typed text, or null if the edit was cancelled.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the entry changes (one undo step), or the label is          */
    /*            simply redrawn.                                             */
    /*------------------------------------------------------------------------*/
    void TREE_CommitInlineEdit(List<int> path, string text)
    {
        VoorheesItem item;                               // the entry
        VoorheesNode newNode;                            // a value's new value
        VoorheesEditStep step;                           // the edit's undo step
        string sError;                                   // why the edit was refused

        item = doc.Vd_ItemAt(path);
        if (text != null && item != null)
        {   /* Something was typed and the entry is still there. */
            step = doc.Vd_BeginStep(path);
            sError = null;
            if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
            {   /* A comment entry: its new words. */
                sError = doc.Vd_ActSetCommentText(step, path, text);
            }
            else if (!item.Vi_node.Vn_IsContainer())
            {   /* A plain value: the same type, from the text. */
                newNode = VoorheesFormat.Vf_ConvertText(doc, item.Vi_node, text);
                if (newNode == null)
                {   /* Not valid for its type. */
                    sError = "\"" + text + "\" is not a valid "
                        + VoorheesFormat.Vf_KindName(doc.Vd_iFormat, item.Vi_node.Vn_iKind).ToLower()
                        + " value.\r\n\r\nTo change the value's type, use the "
                        + "Type box in the edit panel.";
                }
                else
                {   /* Valid: set it. */
                    sError = doc.Vd_ActSetValue(step, path, newNode);
                }
            }
            DOC_Finish(step, sError);
        }

        /* Whatever happened, the node shows its normal label again. */
        tree.Vtv_RefreshLabel(tree.Vtv_NodeAt(path, false, false));
        EDIT_LoadSelected();
    }

    /*------------------------------------------------------------------------*/
    /* TREE_MouseDown:                                                        */
    /*                                                                        */
    /* A right click selects the node under the mouse before the context      */
    /* menu opens, so the menu is for that entry.  Selecting reloads the      */
    /* edit panel, so any unsaved panel changes are settled first.  A right   */
    /* click on empty space is noted (CTX_bEmptySpace) for the menu.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sender : the tree.                                                 */
    /*     e      : the mouse button and position.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the clicked node may become selected.                       */
    /*------------------------------------------------------------------------*/
    void TREE_MouseDown(object sender, MouseEventArgs e)
    {
        TreeNode node;                                   // node under the mouse
        List<int> path;                                  // its path

        if (e.Button != MouseButtons.Right)
        {   /* Only right clicks need this. */
            return;
        }

        node = tree.GetNodeAt(e.X, e.Y);

        /* Remember whether this right click is on empty space: the menu   */
        /* that opens next offers only the top-level additions then.       */
        CTX_bEmptySpace = (node == null || node.Tag == null);

        if (node == null || node.Tag == null || node == tree.SelectedNode)
        {   /* Empty space, or already selected: nothing to change. */
            return;
        }

        path = VoorheesTreeView.Vtv_PathOf(node);
        if (!EDIT_ConfirmPendingPanel())
        {   /* User cancelled at the unsaved-panel question: keep the selection. */
            return;
        }

        /* Found again by path: saving the panel may have changed the   */
        /* tree, making the TreeNode from GetNodeAt stale.              */
        tree.Vtv_Select(path);
    }

    /*------------------------------------------------------------------------*/
    /* SEARCH: find and replace                                               */
    /*------------------------------------------------------------------------*/

    /*-----------------------------------------------------------------------*/
    /* SEARCH_ShowFind:                                                      */
    /*                                                                       */
    /* Opens the Find / Replace window, or brings the open one forward in    */
    /* the requested layout.  It is modeless and has no owner, so it stays   */
    /* usable alongside the editor and is not hidden or closed with it       */
    /* (OnFormClosing closes it explicitly).                                 */
    /*                                                                       */
    /* Arguments:                                                            */
    /*     showReplace : true for Find + Replace, false for Find only.       */
    /*                                                                       */
    /* Returns:                                                              */
    /*     void : the window is shown.                                       */
    /*-----------------------------------------------------------------------*/
    void SEARCH_ShowFind(bool showReplace)
    {
        if (findForm != null && !findForm.IsDisposed)
        {   /* Already open: switch its layout and bring it forward. */
            findForm.SHOW_SetMode(showReplace);
            findForm.BringToFront();
            return;
        }

        /* Not open: create it. */
        findForm = new FindReplaceForm(this, showReplace);
        findForm.Show();
    }

    /*----------------------------------------------------------------------*/
    /* SEARCH_Comparison:                                                   */
    /*                                                                      */
    /* The string comparison for a match-case setting.                      */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     matchCase : true for an exact-case match.                        */
    /*                                                                      */
    /* Returns:                                                             */
    /*     StringComparison : Ordinal or OrdinalIgnoreCase.                 */
    /*----------------------------------------------------------------------*/
    static StringComparison SEARCH_Comparison(bool matchCase)
    {
        if (matchCase)
        {   /* Exact case. */
            return(StringComparison.Ordinal);
        }

        /* Any case. */
        return(StringComparison.OrdinalIgnoreCase);
    }

    /*------------------------------------------------------------------------*/
    /* SEARCH_FindNext:                                                       */
    /*                                                                        */
    /* Selects the next entry after the current one whose key, value or       */
    /* comment contains the search text, wrapping round to the top.  The      */
    /* DOCUMENT is searched (Vd_FindNext), on a worker behind the             */
    /* progress dialog, so entries inside containers never opened are         */
    /* found too; the match is then opened to and selected.  Moving the       */
    /* selection reloads the edit panel, so unsaved panel changes are         */
    /* settled first.                                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     searchText   : text to look for.                                   */
    /*     matchCase    : true for an exact-case match.                       */
    /*     searchKeys   : look in object member keys.                         */
    /*     searchValues : look in plain values and comments.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : 1 when a match was selected, 0 when there is none, and       */
    /*           a negative value when the user cancelled at the              */
    /*           unsaved-panel question.                                      */
    /*------------------------------------------------------------------------*/
    public int SEARCH_FindNext(string searchText, bool matchCase,
        bool searchKeys, bool searchValues)
    {
        VoorheesDocument searchDoc;                      // the document, captured for the worker
        List<int> start;                                 // where the search starts
        List<int> found;                                 // the match
        StringComparison comp;                           // exact or case-insensitive comparison

        if (string.IsNullOrEmpty(searchText))
        {   /* Nothing to look for. */
            return(0);
        }

        if (!EDIT_ConfirmPendingPanel())
        {   /* User cancelled at the unsaved-panel question. */
            return(-1);
        }

        searchDoc = doc;
        start = TREE_SelectedPath();
        comp = SEARCH_Comparison(matchCase);
        found = null;
        PROG_Run("Searching", delegate
        {
            found = searchDoc.Vd_FindNext(start, searchText, comp, searchKeys,
                searchValues);
        });

        if (found == null)
        {   /* Searched everything: no match. */
            return(0);
        }

        /* Match: open to it and select it. */
        tree.Vtv_Select(found);
        return(1);
    }

    /*----------------------------------------------------------------------*/
    /* SEARCH_ReplaceCurrent:                                               */
    /*                                                                      */
    /* Replace button: replaces in the selected entry's key, value and/or   */
    /* comments (the Find window then moves on to the next match), as one   */
    /* undo step (Vd_ReplaceAt).  If part of the match had to be skipped,   */
    /* the user is told why.                                                */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     searchText   : text to replace.                                  */
    /*     replaceText  : text to put in its place.                         */
    /*     matchCase    : true for an exact-case match.                     */
    /*     searchKeys   : replace in the key.                               */
    /*     searchValues : replace in the value and comments.                */
    /*                                                                      */
    /* Returns:                                                             */
    /*     bool : true if anything changed (one undo step).                 */
    /*----------------------------------------------------------------------*/
    public bool SEARCH_ReplaceCurrent(string searchText, string replaceText,
        bool matchCase, bool searchKeys, bool searchValues)
    {
        List<int> path;                                  // the selected entry
        VoorheesEditStep step;                           // the replace's undo step
        int count;                                       // replacements made
        int skipped;                                     // matches refused

        if (string.IsNullOrEmpty(searchText))
        {   /* Nothing to look for. */
            return(false);
        }

        if (!EDIT_ConfirmPendingPanel())
        {   /* User cancelled at the unsaved-panel question. */
            return(false);
        }

        path = TREE_SelectedPath();
        if (path == null)
        {   /* Nothing selected. */
            return(false);
        }

        skipped = 0;
        step = doc.Vd_BeginStep(path);
        count = doc.Vd_ReplaceAt(step, path, searchText, replaceText, matchCase,
            SEARCH_Comparison(matchCase), searchKeys, searchValues, ref skipped);
        DOC_Finish(step, null);
        EDIT_LoadSelected();

        if (skipped > 0)
        {   /* Something matched but was refused: say why. */
            DLG_Message("Part of this match wasn't replaced: the "
                + "result would be an invalid number/boolean, a key "
                + "that's empty or already in use, or a comment that "
                + "can't hold the text (or holds several comments).",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /* Whether anything was replaced. */
        return(count > 0);
    }

    /*----------------------------------------------------------------------*/
    /* SEARCH_ReplaceAll:                                                   */
    /*                                                                      */
    /* Replace All: replaces throughout the document as ONE undo step.      */
    /* The document is walked on a worker behind the progress dialog        */
    /* (Vd_ReplaceAll), then the tree follows each change.  Matches that    */
    /* cannot be replaced are skipped and counted.                          */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     searchText   : text to replace.                                  */
    /*     replaceText  : text to put in its place.                         */
    /*     matchCase    : true for an exact-case match.                     */
    /*     searchKeys   : replace in object member keys.                    */
    /*     searchValues : replace in plain values and comments.             */
    /*     skipped      : set to the number of matches refused.             */
    /*                                                                      */
    /* Returns:                                                             */
    /*     int : replacements made; a negative value when the user          */
    /*           cancelled at the unsaved-panel question.                   */
    /*----------------------------------------------------------------------*/
    public int SEARCH_ReplaceAll(string searchText, string replaceText,
        bool matchCase, bool searchKeys, bool searchValues, out int skipped)
    {
        VoorheesDocument replaceDoc;                     // the document, captured for the worker
        VoorheesEditStep step;                           // Replace All's undo step
        int count;                                       // replacements made
        int iSkippedHere;                                // matches refused, from the worker

        skipped = 0;
        if (string.IsNullOrEmpty(searchText))
        {   /* Nothing to look for. */
            return(0);
        }

        if (!EDIT_ConfirmPendingPanel())
        {   /* User cancelled at the unsaved-panel question. */
            return(-1);
        }

        replaceDoc = doc;
        step = doc.Vd_BeginStep(TREE_SelectedPath());
        count = 0;
        iSkippedHere = 0;
        PROG_Run("Replacing", delegate
        {
            count = replaceDoc.Vd_ReplaceAll(step, searchText, replaceText,
                matchCase, searchKeys, searchValues, out iSkippedHere);
        });

        /* An out parameter cannot be used inside the work, so the count   */
        /* came back through a local.                                      */
        skipped = iSkippedHere;
        DOC_Finish(step, null);
        EDIT_LoadSelected();

        /* Number of replacements. */
        return(count);
    }

    /*------------------------------------------------------------------------*/
    /* DLG: message boxes and a small text input dialog                       */
    /*------------------------------------------------------------------------*/

    /*----------------------------------------------------------------------*/
    /* DLG_HookMessage, DLG_HookInput, DLG_HookFile:                        */
    /*                                                                      */
    /* Partial methods: places where the test harness can answer for the    */
    /* user.  Voorhees.exe has no body for them, so the compiler drops      */
    /* every call and the real dialog always shows; the harness build       */
    /* (tools\VoorheesHarness.cs) implements them, records the question     */
    /* and sets bAnswered with the scripted answer.                         */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sText     : DLG_HookMessage: the message.                        */
    /*     buttons   : DLG_HookMessage: the buttons offered.                */
    /*     sTitle    : DLG_HookInput: the input box's title.                */
    /*     bSave     : DLG_HookFile: a file to save (else one to open).     */
    /*     bAnswered : set to true when the hook answered.                  */
    /*     result    : DLG_HookMessage: the button "pressed".               */
    /*     sAnswer   : DLG_HookInput: the text "typed", or null for Cancel; */
    /*                 DLG_HookFile: the file "chosen", or null.            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the answer, if any, is in the ref arguments.              */
    /*----------------------------------------------------------------------*/
    partial void DLG_HookMessage(string sText, MessageBoxButtons buttons,
        ref bool bAnswered, ref DialogResult result);
    partial void DLG_HookInput(string sTitle, ref bool bAnswered,
        ref string sAnswer);
    partial void DLG_HookFile(bool bSave, ref bool bAnswered, ref string sAnswer);

    /*----------------------------------------------------------------------*/
    /* DLG_HookChoice:                                                      */
    /*                                                                      */
    /* Partial method, like the three above, for the choice-and-text        */
    /* dialog (DLG_ChoiceInputBox).                                         */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     question  : what is asked (its choices and defaults).            */
    /*     bAnswered : set to true when the hook answered.                  */
    /*     iChoice   : the choice "made" (an index into Vnq_arrChoices).    */
    /*     sAnswer   : the text "typed", or null for Cancel.                */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the answer, if any, is in the ref arguments.              */
    /*----------------------------------------------------------------------*/
    partial void DLG_HookChoice(VoorheesNameQuestion question, ref bool bAnswered,
        ref int iChoice, ref string sAnswer);

    /*----------------------------------------------------------------------*/
    /* DLG_AskName:                                                         */
    /*                                                                      */
    /* Asks a format's question for a new entry's name (Vf_NameQuestion):   */
    /* text alone in the input box (DLG_InputBox), or a choice and text in  */
    /* the choice dialog (DLG_ChoiceInputBox); the answer becomes the name  */
    /* as the format says (Vf_NameFromAnswer).                              */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     question : the question.                                         */
    /*                                                                      */
    /* Returns:                                                             */
    /*     string : the name, or null when cancelled.                       */
    /*----------------------------------------------------------------------*/
    string DLG_AskName(VoorheesNameQuestion question)
    {
        string sText;                                    // the text given
        int iChoice;                                     // the choice made, or -1

        iChoice = -1;
        if (question.Vnq_arrChoices == null)
        {   /* Text alone. */
            sText = DLG_InputBox(question.Vnq_sTitle, question.Vnq_sPrompt,
                question.Vnq_sText);
        }
        else if (!DLG_ChoiceInputBox(question, out iChoice, out sText))
        {   /* A choice and text: cancelled. */
            sText = null;
        }

        if (sText == null)
        {   /* Cancelled. */
            return(null);
        }

        /* The name the answer gives. */
        return(VoorheesFormat.Vf_NameFromAnswer(doc.Vd_iFormat, question, iChoice, sText));
    }

    /*----------------------------------------------------------------------*/
    /* DLG_ChoiceInputBox:                                                  */
    /*                                                                      */
    /* A small modal dialog asking for a choice from a fixed list and a     */
    /* line of text (a registry key's hive and path): the choice's label    */
    /* and a drop-down list (nothing else can be entered there), the        */
    /* prompt and the text box holding the starting text (all selected,     */
    /* ready to type over), OK and Cancel.  OK is tracked with a flag, not  */
    /* DialogResult; Enter means OK and Escape Cancel; laid out from        */
    /* measured sizes, again on any resize (DLG_LayoutChoiceBox).  The test */
    /* harness can answer instead (DLG_HookChoice).                         */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     question : the question (title, choices, defaults).              */
    /*     iChoice  : set to the choice made.                               */
    /*     sText    : set to the text, or null when cancelled.              */
    /*                                                                      */
    /* Returns:                                                             */
    /*     bool : true for OK, false for Cancel.                            */
    /*----------------------------------------------------------------------*/
    bool DLG_ChoiceInputBox(VoorheesNameQuestion question, out int iChoice,
        out string sText)
    {
        Form dlg;                                        // the dialog
        ComboBox cmbChoice;                              // the drop-down list
        TextBox txt;                                     // the text box
        Button btnOk;                                    // OK button
        Button btnCancel;                                // Cancel button
        bool bSaved;                                     // OK was pressed
        bool bAnswered;                                  // the test harness answered
        int iAnswer;                                     // its choice
        string sAnswer;                                  // its text

        iChoice = question.Vnq_iChoice;
        sText = null;
        bAnswered = false;
        iAnswer = question.Vnq_iChoice;
        sAnswer = null;
        DLG_HookChoice(question, ref bAnswered, ref iAnswer, ref sAnswer);
        if (bAnswered)
        {   /* Answered by the harness: no window. */
            iChoice = iAnswer;
            sText = sAnswer;
            return(sAnswer != null);
        }

        bSaved = false;
        dlg = DLG_BuildChoiceBox(question, out cmbChoice, out txt, out btnOk,
            out btnCancel);
        using (dlg)
        {
            /* OK records the answer before closing; Cancel just closes. */
            btnOk.Click += delegate
            {
                bSaved = true;
                dlg.Close();
            };
            btnCancel.Click += delegate { dlg.Close(); };
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            /* The text box has the focus, its text selected to type over. */
            dlg.Shown += delegate
            {
                txt.Focus();
                txt.SelectAll();
            };

            dlg.ShowDialog(this);

            if (bSaved)
            {   /* OK: the choice and the text (read before the dialog is disposed). */
                iChoice = cmbChoice.SelectedIndex;
                sText = txt.Text;
                return(true);
            }
        }

        /* Cancelled or closed. */
        return(false);
    }

    /*----------------------------------------------------------------------*/
    /* DLG_BuildChoiceBox:                                                  */
    /*                                                                      */
    /* Builds the choice dialog (DLG_ChoiceInputBox), not shown and with    */
    /* OK and Cancel not yet wired: a small fixed dialog centred on the     */
    /* editor, the choice's label and the drop-down list with the default   */
    /* chosen, the prompt and the text box with the starting text, OK and   */
    /* Cancel; laid out from measured sizes now and on any resize.  A       */
    /* function of its own so the test harness can check and picture the    */
    /* dialog without showing it.                                           */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     question  : the question.                                        */
    /*     cmbChoice : receives the drop-down list.                         */
    /*     txt       : receives the text box.                               */
    /*     btnOk     : receives OK.                                         */
    /*     btnCancel : receives Cancel.                                     */
    /*                                                                      */
    /* Returns:                                                             */
    /*     Form : the dialog (the caller disposes of it).                   */
    /*----------------------------------------------------------------------*/
    public static Form DLG_BuildChoiceBox(VoorheesNameQuestion question,
        out ComboBox cmbChoice, out TextBox txt, out Button btnOk, out Button btnCancel)
    {
        Form dlg;                                        // the dialog
        Label lblChoice;                                 // the choice's label
        Label lblPrompt;                                 // the text's label
        ComboBox cmbLocal;                               // the list (a local: an out argument cannot be used in the resize handler)
        TextBox txtLocal;                                // the text box, likewise
        Button btnOkLocal;                               // OK, likewise
        Button btnCancelLocal;                           // Cancel, likewise

        /* A small fixed dialog, centred on the editor, no taskbar button. */
        dlg = new Form();
        dlg.Text = question.Vnq_sTitle;
        dlg.StartPosition = FormStartPosition.CenterParent;
        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
        dlg.MinimizeBox = false;
        dlg.MaximizeBox = false;
        dlg.ShowInTaskbar = false;

        /* The choice: its label and the list, the default chosen. */
        lblChoice = new Label();
        lblChoice.Text = question.Vnq_sChoiceLabel;
        lblChoice.AutoSize = true;
        dlg.Controls.Add(lblChoice);

        cmbLocal = new ComboBox();
        cmbLocal.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbLocal.Items.AddRange(question.Vnq_arrChoices);
        if (question.Vnq_iChoice >= 0 && question.Vnq_iChoice < cmbLocal.Items.Count)
        {   /* The default entry. */
            cmbLocal.SelectedIndex = question.Vnq_iChoice;
        }
        else
        {   /* No default: the first. */
            cmbLocal.SelectedIndex = 0;
        }
        dlg.Controls.Add(cmbLocal);

        /* The text: its label and the box with the starting text. */
        lblPrompt = new Label();
        lblPrompt.Text = question.Vnq_sPrompt;
        lblPrompt.AutoSize = true;
        dlg.Controls.Add(lblPrompt);

        txtLocal = new TextBox();
        txtLocal.Text = question.Vnq_sText;
        dlg.Controls.Add(txtLocal);

        /* OK and Cancel, bottom right. */
        btnOkLocal = new Button();
        btnOkLocal.Text = "OK";
        dlg.Controls.Add(btnOkLocal);

        btnCancelLocal = new Button();
        btnCancelLocal.Text = "Cancel";
        dlg.Controls.Add(btnCancelLocal);

        /* Sized to the measured content, placed now and on any resize. */
        DLG_LayoutChoiceBox(dlg, lblChoice, cmbLocal, lblPrompt, txtLocal, btnOkLocal,
            btnCancelLocal, true);
        dlg.Resize += delegate
        {
            DLG_LayoutChoiceBox(dlg, lblChoice, cmbLocal, lblPrompt, txtLocal,
                btnOkLocal, btnCancelLocal, false);
        };

        cmbChoice = cmbLocal;
        txt = txtLocal;
        btnOk = btnOkLocal;
        btnCancel = btnCancelLocal;

        /* Built, not shown. */
        return(dlg);
    }

    /*----------------------------------------------------------------------*/
    /* DLG_LayoutChoiceBox:                                                 */
    /*                                                                      */
    /* Places the choice dialog's controls: the choice's label, the list    */
    /* under it (as wide as its longest entry plus the drop-down button),   */
    /* the prompt, the text box across the full width, then OK and Cancel   */
    /* at the bottom right, Cancel outermost; DLG_PAD round it all and      */
    /* DLG_GAP between the rows.  Asked to, it first sizes the dialog to    */
    /* the content: as wide as the widest of the list, the prompt, the      */
    /* starting text with room to type, DLG_MINCHARS characters and the     */
    /* two buttons.                                                         */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     dlg        : the dialog.                                         */
    /*     lblChoice  : the choice's label.                                 */
    /*     cmbChoice  : the list.                                           */
    /*     lblPrompt  : the text's label.                                   */
    /*     txt        : the text box.                                       */
    /*     btnOk      : OK button.                                          */
    /*     btnCancel  : Cancel button.                                      */
    /*     bSizeIt    : size the dialog to the content first.               */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the controls are placed (and the dialog sized).           */
    /*----------------------------------------------------------------------*/
    static void DLG_LayoutChoiceBox(Form dlg, Label lblChoice, ComboBox cmbChoice,
        Label lblPrompt, TextBox txt, Button btnOk, Button btnCancel, bool bSizeIt)
    {
        Size szButton;                                   // size of each button
        int iListWidth;                                  // the list's width
        int iWidth;                                      // content width
        int y;                                           // the next row's top
        int i;

        szButton = DLG_ButtonSize(new Button[] { btnOk, btnCancel });

        /* The list: its longest entry, plus the drop-down button and margins. */
        iListWidth = 0;
        for (i = 0; i < cmbChoice.Items.Count; i++)
        {
            iListWidth = Math.Max(iListWidth, TextRenderer.MeasureText(
                cmbChoice.Items[i].ToString(), cmbChoice.Font).Width);
        }
        iListWidth += SystemInformation.VerticalScrollBarWidth + DLG_GAP * 2;

        if (bSizeIt)
        {   /* The dialog sized to its content: the widest row, then the rows' heights. */
            iWidth = Math.Max(iListWidth, lblChoice.PreferredSize.Width);
            iWidth = Math.Max(iWidth, lblPrompt.PreferredSize.Width);
            iWidth = Math.Max(iWidth, TextRenderer.MeasureText(txt.Text,
                txt.Font).Width + txt.Font.Height * 2);
            iWidth = Math.Max(iWidth, TextRenderer.MeasureText(
                new string('x', DLG_MINCHARS), txt.Font).Width);
            iWidth = Math.Max(iWidth, szButton.Width * 2 + DLG_GAP);
            dlg.ClientSize = new Size(iWidth + DLG_PAD * 2,
                DLG_PAD + lblChoice.PreferredSize.Height + DLG_GAP + cmbChoice.Height
                + DLG_PAD + lblPrompt.PreferredSize.Height + DLG_GAP + txt.Height
                + DLG_PAD + szButton.Height + DLG_PAD);
        }

        /* The choice, then the text, top down. */
        y = DLG_PAD;
        lblChoice.Location = new Point(DLG_PAD, y);
        y += lblChoice.PreferredSize.Height + DLG_GAP;
        cmbChoice.SetBounds(DLG_PAD, y, iListWidth, cmbChoice.Height);
        y += cmbChoice.Height + DLG_PAD;
        lblPrompt.Location = new Point(DLG_PAD, y);
        y += lblPrompt.PreferredSize.Height + DLG_GAP;
        txt.SetBounds(DLG_PAD, y, dlg.ClientSize.Width - DLG_PAD * 2, txt.Height);

        /* Buttons along the bottom, right-aligned. */
        y = dlg.ClientSize.Height - DLG_PAD - szButton.Height;
        btnCancel.SetBounds(dlg.ClientSize.Width - DLG_PAD - szButton.Width, y,
            szButton.Width, szButton.Height);
        btnOk.SetBounds(btnCancel.Left - DLG_GAP - szButton.Width, y,
            szButton.Width, szButton.Height);
    }

    /*----------------------------------------------------------------------*/
    /* DLG_Message:                                                         */
    /*                                                                      */
    /* Every message box the editor window shows goes through here: the     */
    /* standard Windows message box with the "Voorhees" caption, unless     */
    /* the test harness answers it (DLG_HookMessage).                       */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sText   : the message.                                           */
    /*     buttons : the buttons to offer.                                  */
    /*     icon    : the icon.                                              */
    /*                                                                      */
    /* Returns:                                                             */
    /*     DialogResult : the button pressed.                               */
    /*----------------------------------------------------------------------*/
    DialogResult DLG_Message(string sText, MessageBoxButtons buttons,
        MessageBoxIcon icon)
    {
        bool bAnswered;                                  // the harness answered
        DialogResult result;                             // its answer

        bAnswered = false;
        result = DialogResult.None;
        DLG_HookMessage(sText, buttons, ref bAnswered, ref result);
        if (bAnswered)
        {   /* Answered by the harness: no window. */
            return(result);
        }

        /* The user's answer. */
        return(MessageBox.Show(sText, "Voorhees", buttons, icon));
    }

    /*------------------------------------------------------------------------*/
    /* DLG_InputBox:                                                          */
    /*                                                                        */
    /* Asks for one line of text in a small modal dialog with OK and          */
    /* Cancel.  Whether OK was pressed is tracked with a flag set by the      */
    /* OK button, not with DialogResult, which behaves differently for        */
    /* modal and modeless windows.  Enter means OK and Escape means           */
    /* Cancel.  The test harness can answer instead (DLG_HookInput).          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     title        : dialog title.                                       */
    /*     prompt       : text above the input box.                           */
    /*     defaultValue : initial text in the box.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text entered, or null if cancelled.                   */
    /*------------------------------------------------------------------------*/
    string DLG_InputBox(string title, string prompt, string defaultValue)
    {
        bool saved;                                      // OK was pressed
        Form dlg;                                        // the dialog
        Label lbl;                                       // the prompt
        TextBox txt;                                     // the input box
        Button btnOk;                                    // OK button
        Button btnCancel;                                // Cancel button
        bool bAnswered;                                  // the test harness answered
        string sAnswer;                                  // its answer

        bAnswered = false;
        sAnswer = null;
        DLG_HookInput(title, ref bAnswered, ref sAnswer);
        if (bAnswered)
        {   /* Answered by the harness: no window. */
            return(sAnswer);
        }

        saved = false;

        dlg = new Form();
        using (dlg)
        {
            /* A small fixed-size dialog, centred on the editor, with   */
            /* no taskbar button of its own.                            */
            dlg.Text = title;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MinimizeBox = false;
            dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;

            /* The prompt, and below it the box holding the default. */
            lbl = new Label();
            lbl.Text = prompt;
            lbl.AutoSize = true;
            dlg.Controls.Add(lbl);

            txt = new TextBox();
            txt.Text = defaultValue;
            dlg.Controls.Add(txt);

            /* OK and Cancel, side by side under the box on the right. */
            btnOk = new Button();
            btnOk.Text = "OK";
            dlg.Controls.Add(btnOk);

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            dlg.Controls.Add(btnCancel);

            /* Size the window to its measured content, then place the   */
            /* controls; the same placing runs again on any resize.      */
            dlg.ClientSize = DLG_InputBoxSize(lbl, txt, btnOk, btnCancel);
            DLG_LayoutInputBox(dlg, lbl, txt, btnOk, btnCancel);
            dlg.Resize += delegate
            {
                DLG_LayoutInputBox(dlg, lbl, txt, btnOk, btnCancel);
            };

            /* OK records the answer before closing; Cancel just closes. */
            btnOk.Click += delegate
            {
                saved = true;
                dlg.Close();
            };
            btnCancel.Click += delegate { dlg.Close(); };
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            dlg.ShowDialog(this);

            if (saved)
            {   /* OK: hand back what was typed (read before the dialog is disposed). */
                return(txt.Text);
            }
        }

        /* Cancelled or closed. */
        return(null);
    }

    /*----------------------------------------------------------------------*/
    /* Input dialog spacing, pixels, and the narrowest input box, in        */
    /* characters of the dialog's font.  Everything else is measured.       */
    /*----------------------------------------------------------------------*/

    const int DLG_PAD = 12;                              // space round the content
    const int DLG_GAP = 6;                               // space between the rows and between the buttons
    const int DLG_MINCHARS = 40;                         // input box is at least this many average characters wide

    /*----------------------------------------------------------------------*/
    /* DLG_ButtonSize:                                                      */
    /*                                                                      */
    /* One size for a row of buttons: wide enough for the longest caption   */
    /* plus padding and tall enough for the font, never smaller than the    */
    /* standard 75 x 23 dialog button.                                      */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     arrButtons : the buttons in the row; their text and font are     */
    /*                  measured.                                           */
    /*                                                                      */
    /* Returns:                                                             */
    /*     Size : the size every button in the row should have.             */
    /*----------------------------------------------------------------------*/
    public static Size DLG_ButtonSize(Button[] arrButtons)
    {
        int iWidth;                                      // widest caption plus padding
        int iHeight;                                     // tallest font plus padding
        int i;

        /* The standard dialog button size is the smallest allowed. */
        iWidth = 75;
        iHeight = 23;
        for (i = 0; i < arrButtons.Length; i++)
        {
            iWidth = Math.Max(iWidth, TextRenderer.MeasureText(
                arrButtons[i].Text, arrButtons[i].Font).Width + 24);
            iHeight = Math.Max(iHeight, arrButtons[i].Font.Height + 10);
        }

        /* Big enough for every caption in the row. */
        return(new Size(iWidth, iHeight));
    }

    /*----------------------------------------------------------------------*/
    /* DLG_SmallIconBitmap:                                                 */
    /*                                                                      */
    /* A system icon (SystemIcons.Warning, .Information ...) as a picture   */
    /* at the system's small icon size, for a note in a dialog.  The sized  */
    /* copy of the shared icon is made, turned into the picture and freed   */
    /* (the shared icon itself is never freed).                             */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     icon : the system icon.                                          */
    /*                                                                      */
    /* Returns:                                                             */
    /*     Bitmap : the picture (the caller disposes of it).                */
    /*----------------------------------------------------------------------*/
    public static Bitmap DLG_SmallIconBitmap(Icon icon)
    {
        Icon iconSmall;                                  // the icon at the small size
        Bitmap bmp;                                      // its picture

        iconSmall = new Icon(icon, SystemInformation.SmallIconSize);
        bmp = iconSmall.ToBitmap();
        iconSmall.Dispose();

        /* The picture. */
        return(bmp);
    }

    /*----------------------------------------------------------------------*/
    /* DLG_NewEtchedLine:                                                   */
    /*                                                                      */
    /* An etched horizontal line for a dialog (a label two pixels high with */
    /* a sunken border: the classic Windows separator), placed by the       */
    /* dialog's layout.                                                     */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     parent : the control it goes in.                                 */
    /*                                                                      */
    /* Returns:                                                             */
    /*     Label : the line, added.                                         */
    /*----------------------------------------------------------------------*/
    public static Label DLG_NewEtchedLine(Control parent)
    {
        Label ln;                                        // the new line

        ln = new Label();
        ln.AutoSize = false;
        ln.BorderStyle = BorderStyle.Fixed3D;
        ln.Height = 2;
        parent.Controls.Add(ln);

        /* Placed later. */
        return(ln);
    }

    /*----------------------------------------------------------------------*/
    /* DLG_InputBoxSize:                                                    */
    /*                                                                      */
    /* The client size the input dialog needs: as wide as the widest of     */
    /* the prompt, the default text (with room to spare) and DLG_MINCHARS   */
    /* characters, and tall enough for the prompt, the box and the          */
    /* buttons, with DLG_PAD round them.                                    */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     lbl       : the prompt.                                          */
    /*     txt       : the input box, holding the default text.             */
    /*     btnOk     : OK button.                                           */
    /*     btnCancel : Cancel button.                                       */
    /*                                                                      */
    /* Returns:                                                             */
    /*     Size : the client size to give the dialog.                       */
    /*----------------------------------------------------------------------*/
    static Size DLG_InputBoxSize(Label lbl, TextBox txt, Button btnOk,
        Button btnCancel)
    {
        Size szButton;                                   // size of each button
        int iWidth;                                      // content width
        int iHeight;                                     // content height

        szButton = DLG_ButtonSize(new Button[] { btnOk, btnCancel });

        /* Widest of: the prompt, the default text with room to type,   */
        /* a minimum number of characters, and the two buttons.         */
        iWidth = lbl.PreferredSize.Width;
        iWidth = Math.Max(iWidth, TextRenderer.MeasureText(txt.Text,
            txt.Font).Width + txt.Font.Height * 2);
        iWidth = Math.Max(iWidth, TextRenderer.MeasureText(
            new string('x', DLG_MINCHARS), txt.Font).Width);
        iWidth = Math.Max(iWidth, szButton.Width * 2 + DLG_GAP);

        /* Prompt, box and button rows, with gaps between. */
        iHeight = lbl.PreferredSize.Height + DLG_GAP + txt.Height
            + DLG_PAD + szButton.Height;

        /* Content plus padding all round. */
        return(new Size(iWidth + DLG_PAD * 2, iHeight + DLG_PAD * 2));
    }

    /*----------------------------------------------------------------------*/
    /* DLG_LayoutInputBox:                                                  */
    /*                                                                      */
    /* Places the input dialog's controls from the dialog's current client  */
    /* size: the prompt at the top left, the box below it across the full   */
    /* width, OK and Cancel at the bottom right with Cancel outermost.      */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     dlg       : the dialog.                                          */
    /*     lbl       : the prompt.                                          */
    /*     txt       : the input box.                                       */
    /*     btnOk     : OK button.                                           */
    /*     btnCancel : Cancel button.                                       */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the controls are moved and sized.                         */
    /*----------------------------------------------------------------------*/
    static void DLG_LayoutInputBox(Form dlg, Label lbl, TextBox txt,
        Button btnOk, Button btnCancel)
    {
        Size szButton;                                   // size of each button
        int iButtonTop;                                  // y of the button row

        szButton = DLG_ButtonSize(new Button[] { btnOk, btnCancel });

        /* Prompt, then the box under it, full width. */
        lbl.Location = new Point(DLG_PAD, DLG_PAD);
        txt.SetBounds(DLG_PAD, DLG_PAD + lbl.PreferredSize.Height + DLG_GAP,
            dlg.ClientSize.Width - DLG_PAD * 2, txt.Height);

        /* Buttons along the bottom, right-aligned. */
        iButtonTop = dlg.ClientSize.Height - DLG_PAD - szButton.Height;
        btnCancel.SetBounds(dlg.ClientSize.Width - DLG_PAD - szButton.Width,
            iButtonTop, szButton.Width, szButton.Height);
        btnOk.SetBounds(btnCancel.Left - DLG_GAP - szButton.Width,
            iButtonTop, szButton.Width, szButton.Height);
    }

    /*------------------------------------------------------------------------*/
    /* CMD: command line hand-over                                            */
    /*------------------------------------------------------------------------*/

    /*----------------------------------------------------------------------*/
    /* CMD_OpenFileOnLoad:                                                  */
    /*                                                                      */
    /* Called by Main with a file named on the command line.  The file is   */
    /* only remembered here and opened in OnLoad, once the window has its   */
    /* real size and the saved settings are applied.                        */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     path : the file to open.                                         */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : pendingLoadPath is set.                                   */
    /*----------------------------------------------------------------------*/
    public void CMD_OpenFileOnLoad(string path)
    {
        pendingLoadPath = path;
    }

    /*------------------------------------------------------------------------*/
    /* OnLoad:                                                                */
    /*                                                                        */
    /* The window is about to appear for the first time.  Applies the         */
    /* saved settings (size, position, font size, splitter ratio, raw         */
    /* view, coloured keys), then opens the file from the command line, if    */
    /* any.                                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     e : passed on to the base class.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window is set up for display.                           */
    /*------------------------------------------------------------------------*/
    protected override void OnLoad(EventArgs e)
    {
        int dist;                                        // splitter position for the saved ratio, pixels

        base.OnLoad(e);
        CFG_Load();
        VIEW_ApplyFontSize(fontSize);
        tree.Vtv_SetShowRaw(VIEW_tsmShowRaw.Checked);
        tree.Vtv_SetColorKeys(VIEW_tsmColorKeys.Checked);

        if (splitContainer.Width > 0)
        {   /* The splitter has a real width: put the bar at the saved ratio. */
            dist = (int)(splitContainer.Width * splitRatio);
            if (dist >= splitContainer.Panel1MinSize
                && dist <= splitContainer.Width - splitContainer.Panel2MinSize)
            {   /* Within the panes' minimum sizes: SplitterDistance would throw otherwise. */
                splitContainer.SplitterDistance = dist;
            }
        }

        if (!string.IsNullOrEmpty(pendingLoadPath))
        {   /* A file came from the command line: open it now, as --format says or as detected. */
            FILE_LoadFromPath(pendingLoadPath, Program.APP_iFormat);
            pendingLoadPath = null;
        }
        else if (Program.APP_iFormat != VoorheesFormat.VF_DETECT
            && VoorheesFormat.Vf_IsAvailable(Program.APP_iFormat))
        {   /* --format with no file: a new document of that format. */
            FILE_New(Program.APP_iFormat);
        }
    }

    /*------------------------------------------------------------------------*/
    /* CFG: settings kept in the registry                                     */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Registry key for Voorhees' own settings.  HKCU, so no                  */
    /* administrator rights are needed.                                       */
    /*------------------------------------------------------------------------*/

    static readonly string CFG_REGPATH = "Software\\Voorhees";  // under HKEY_CURRENT_USER

    /*----------------------------------------------------------------------*/
    /* CFG_HookSkip:                                                        */
    /*                                                                      */
    /* Partial method: the test harness implements it to keep the editor    */
    /* away from the user's saved settings (a scripted run must neither     */
    /* pick up the user's font size or raw-text option nor overwrite their  */
    /* window placement).  Voorhees.exe has no body for it, so the calls    */
    /* are removed and the settings are always used.                        */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     bSkip : set to true to skip loading or saving the settings.      */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the decision is in bSkip.                                 */
    /*----------------------------------------------------------------------*/
    partial void CFG_HookSkip(ref bool bSkip);

    /*----------------------------------------------------------------------*/
    /* CFG_Save:                                                            */
    /*                                                                      */
    /* Saves the window's placement, the splitter ratio, the font size, the */
    /* raw-text view option and the coloured-keys option to the registry,   */
    /* for CFG_Load next time.                                              */
    /* The NORMAL (restored) bounds are saved even while maximized or       */
    /* minimized, so the window restores to a sensible size; whether it was */
    /* maximized is saved separately.  Numbers are written with "." as the  */
    /* decimal point so they read back the same under any locale.  Failure  */
    /* is ignored: losing the window position is not worth an error         */
    /* message.                                                             */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : HKCU\Software\Voorhees is updated.                        */
    /*----------------------------------------------------------------------*/
    void CFG_Save()
    {
        Rectangle bounds;                                // normal (restored) window bounds
        RegistryKey key;                                 // HKCU\Software\Voorhees
        int iMaximized;                                  // 1 when maximized, else 0
        int iShowRaw;                                    // 1 when raw labels are on, else 0
        int iColorKeys;                                  // 1 when coloured keys are on, else 0
        bool bSkip;                                      // the test harness keeps the settings untouched

        bSkip = false;
        CFG_HookSkip(ref bSkip);
        if (bSkip)
        {   /* A harness run: leave the user's settings alone. */
            return;
        }

        try
        {
            if (WindowState == FormWindowState.Normal)
            {   /* Normal window: its current bounds are the ones to keep. */
                bounds = new Rectangle(Location, Size);
            }
            else
            {   /* Maximized or minimized: keep the bounds it restores to. */
                bounds = RestoreBounds;
            }

            if (WindowState == FormWindowState.Maximized)
            {   /* Reopen maximized. */
                iMaximized = 1;
            }
            else
            {   /* Reopen at the saved bounds. */
                iMaximized = 0;
            }

            if (VIEW_tsmShowRaw.Checked)
            {   /* Raw labels on. */
                iShowRaw = 1;
            }
            else
            {   /* Friendly labels. */
                iShowRaw = 0;
            }

            if (VIEW_tsmColorKeys.Checked)
            {   /* Coloured keys on. */
                iColorKeys = 1;
            }
            else
            {   /* Keys in the normal colour. */
                iColorKeys = 0;
            }

            /* Window placement and the view options, as DWORDs. */
            key = Registry.CurrentUser.CreateSubKey(CFG_REGPATH);
            key.SetValue("X", bounds.X);
            key.SetValue("Y", bounds.Y);
            key.SetValue("W", bounds.Width);
            key.SetValue("H", bounds.Height);
            key.SetValue("Max", iMaximized);
            key.SetValue("ShowRaw", iShowRaw);
            key.SetValue("ColorKeys", iColorKeys);

            /* Splitter ratio and font size, as invariant-culture text   */
            /* (registry values have no floating-point type).            */
            key.SetValue("SplitRatio", splitRatio.ToString("F4",
                CultureInfo.InvariantCulture));
            key.SetValue("FontSize", fontSize.ToString("F2",
                CultureInfo.InvariantCulture));
            key.Close();
        }
        catch
        {   /* Registry not writable: not worth bothering the user about. */
        }
    }

    /*----------------------------------------------------------------------*/
    /* CFG_Load:                                                            */
    /*                                                                      */
    /* Reads the settings CFG_Save wrote.  Each value is used only if it    */
    /* is present and sensible:                                             */
    /*     o size and position: all four present, at least 400 x 300, and   */
    /*       still on one of today's monitors (a monitor unplugged since    */
    /*       would otherwise leave the window out of sight);                */
    /*     o maximized flag;                                                */
    /*     o splitter ratio: between 0.1 and 0.95;                          */
    /*     o font size: within VIEW_FONTMIN..VIEW_FONTMAX;                  */
    /*     o raw-text view option: 1 for on;                                */
    /*     o coloured-keys option: 1 for on (off by default).               */
    /* Anything missing or out of range keeps its default.  Failure is      */
    /* ignored and every default kept.                                      */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : window placement, splitRatio, fontSize and the View menu  */
    /*            ticks may change.                                         */
    /*----------------------------------------------------------------------*/
    void CFG_Load()
    {
        RegistryKey key;                                 // HKCU\Software\Voorhees
        object oX;                                       // saved left
        object oY;                                       // saved top
        object oW;                                       // saved width
        object oH;                                       // saved height
        object oMax;                                     // saved maximized flag
        object oRatio;                                   // saved splitter ratio
        object oFont;                                    // saved font size
        object oShowRaw;                                 // saved raw-text view option
        object oColorKeys;                               // saved coloured-keys option
        float size;                                      // font size read back
        double ratio;                                    // splitter ratio read back
        Rectangle saved;                                 // saved bounds
        bool bSkip;                                      // the test harness keeps the defaults

        bSkip = false;
        CFG_HookSkip(ref bSkip);
        if (bSkip)
        {   /* A harness run: the defaults, whatever the user saved. */
            return;
        }

        try
        {
            key = Registry.CurrentUser.OpenSubKey(CFG_REGPATH);
            if (key == null)
            {   /* First run: nothing saved yet, keep the defaults. */
                return;
            }

            oX = key.GetValue("X");
            oY = key.GetValue("Y");
            oW = key.GetValue("W");
            oH = key.GetValue("H");
            oMax = key.GetValue("Max");
            oRatio = key.GetValue("SplitRatio");
            oFont = key.GetValue("FontSize");
            oShowRaw = key.GetValue("ShowRaw");
            oColorKeys = key.GetValue("ColorKeys");
            CFG_LoadColors(key);
            key.Close();

            if (oFont != null)
            {   /* Font size saved: use it if it reads and is in range. */
                if (float.TryParse(oFont.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out size))
                {   /* Read: now check the range. */
                    if (size >= VIEW_FONTMIN && size <= VIEW_FONTMAX)
                    {   /* Sensible size. */
                        fontSize = size;
                    }
                }
            }

            if (oX != null && oY != null && oW != null && oH != null)
            {   /* Placement saved: use it if it is a real size and still on screen. */
                saved = new Rectangle((int)oX, (int)oY, (int)oW, (int)oH);
                if (saved.Width >= 400 && saved.Height >= 300
                    && CFG_IsOnScreen(saved))
                {   /* Usable: place the window there. */
                    StartPosition = FormStartPosition.Manual;
                    Location = saved.Location;
                    Size = saved.Size;
                }
            }

            if (oMax != null && (int)oMax == 1)
            {   /* It was maximized: open maximized. */
                WindowState = FormWindowState.Maximized;
            }

            if (oRatio != null)
            {   /* Splitter ratio saved: use it if it reads and leaves both panes visible. */
                if (double.TryParse(oRatio.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out ratio))
                {   /* Read: now check the range. */
                    if (ratio > 0.1 && ratio < 0.95)
                    {   /* Sensible ratio. */
                        splitRatio = ratio;
                    }
                }
            }

            if (oShowRaw != null && oShowRaw is int)
            {   /* Raw-text view option saved: 1 is on. */
                VIEW_tsmShowRaw.Checked = ((int)oShowRaw == 1);
            }

            if (oColorKeys != null && oColorKeys is int)
            {   /* Coloured-keys option saved: 1 is on. */
                VIEW_tsmColorKeys.Checked = ((int)oColorKeys == 1);
            }
        }
        catch
        {   /* Unreadable or malformed settings: keep the defaults. */
        }
    }

    /*------------------------------------------------------------------------*/
    /* CFG_IsOnScreen:                                                        */
    /*                                                                        */
    /* True if enough of a window rectangle to grab its title bar (at         */
    /* least 100 x 50 pixels) lies within some monitor's working area.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bounds : the window rectangle.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true if it is usefully visible on some monitor.             */
    /*------------------------------------------------------------------------*/
    bool CFG_IsOnScreen(Rectangle bounds)
    {
        Rectangle visible;                               // part of bounds on one monitor

        foreach (Screen screen in Screen.AllScreens)
        {
            visible = Rectangle.Intersect(screen.WorkingArea, bounds);
            if (visible.Width >= 100 && visible.Height >= 50)
            {   /* Enough of it on this monitor. */
                return(true);
            }
        }

        /* Off every monitor, or only a sliver showing. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* CFG_GetString:                                                         */
    /*                                                                        */
    /* Reads one string value from HKCU\Software\Voorhees.                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     name : value name.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the value, or null if the key or value is missing or      */
    /*              not a string.                                             */
    /*------------------------------------------------------------------------*/
    static string CFG_GetString(string name)
    {
        RegistryKey key;                                 // HKCU\Software\Voorhees
        string value;                                    // the value read

        key = Registry.CurrentUser.OpenSubKey(CFG_REGPATH);
        if (key == null)
        {   /* No settings key at all. */
            return(null);
        }

        value = key.GetValue(name) as string;
        key.Close();

        /* The string, or null when absent. */
        return(value);
    }

    /*------------------------------------------------------------------------*/
    /* CFG_SetString:                                                         */
    /*                                                                        */
    /* Writes one string value under HKCU\Software\Voorhees, or deletes       */
    /* it when given null.                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     name  : value name.                                                */
    /*     value : text to store, or null to delete the value.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the registry is updated.                                    */
    /*------------------------------------------------------------------------*/
    static void CFG_SetString(string name, string value)
    {
        RegistryKey key;                                 // HKCU\Software\Voorhees

        key = Registry.CurrentUser.CreateSubKey(CFG_REGPATH);
        if (value == null)
        {   /* Delete it (no error if it is already gone). */
            key.DeleteValue(name, false);
        }
        else
        {   /* Store it. */
            key.SetValue(name, value);
        }
        key.Close();
    }

    /*------------------------------------------------------------------------*/
    /* REG: Explorer integration                                              */
    /*                                                                        */
    /* Everything is under HKCU\Software\Classes, so no administrator         */
    /* rights are needed and only the current user is affected.  For each of  */
    /* .json .ini .cfg .conf .reg, registering adds Voorhees' own file type   */
    /* (a ProgId per extension), "Edit with Voorhees" on the right-click menu */
    /* (under SystemFileAssociations, so it shows WHICHEVER program is the    */
    /* default) and an "Open with" entry.  It changes no default by itself:   */
    /* making Voorhees an extension's default is a separate, explicit choice  */
    /* (Tools > Set Voorhees as Default..., --set-default), which saves the   */
    /* previous default per extension so it can be put back.  On Windows 10   */
    /* and 11 the user's Default Apps choice can still override it.           */
    /*------------------------------------------------------------------------*/

    const string REG_CLASSES = "Software\\Classes\\";                      // HKCU file type registrations
    const string REG_SFA = "Software\\Classes\\SystemFileAssociations\\";  // per-extension verbs, whatever the default

    static readonly string[] REG_EXTENSIONS = new string[]
    {
        ".json",                                         // JSON (and the default before 2.x registrations)
        ".ini",                                          // INI
        ".cfg",                                          // Klipper's printer.cfg and its includes
        ".conf",                                         // Moonraker / KlipperScreen
        ".reg"                                           // registry files (double-click would merge them)
    };

    /*------------------------------------------------------------------------*/
    /* What each of REG_EXTENSIONS is, in the same order, for the Set as      */
    /* Default dialog (beside each extension's checkbox).                     */
    /*------------------------------------------------------------------------*/

    public static readonly string[] REG_DESCRIPTIONS = new string[]
    {
        "JSON and JSONC documents",                      // .json
        "INI settings files",                            // .ini
        "Klipper printer configs",                       // .cfg
        "Moonraker and KlipperScreen configs",           // .conf
        "Registry files"                                 // .reg
    };

    /*------------------------------------------------------------------------*/
    /* REG_ProgId:                                                            */
    /*                                                                        */
    /* Voorhees' own file type (ProgId) for an extension: Voorhees.JsonFile   */
    /* (as 2.0 registered it), Voorhees.IniFile, Voorhees.CfgFile,            */
    /* Voorhees.ConfFile, Voorhees.RegFile.                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sExt : the extension with its dot, lower case.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the ProgId.                                               */
    /*------------------------------------------------------------------------*/
    static string REG_ProgId(string sExt)
    {
        /* "Voorhees." + the extension with a capital + "File". */
        return("Voorhees." + char.ToUpperInvariant(sExt[1]) + sExt.Substring(2)
            + "File");
    }

    /*------------------------------------------------------------------------*/
    /* REG_PrevValue:                                                         */
    /*                                                                        */
    /* The settings value that keeps an extension's default from before       */
    /* Voorhees took it: PrevJsonProgId (as 2.0 named it), PrevIniProgId ...  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sExt : the extension with its dot, lower case.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the value's name under HKCU\Software\Voorhees.            */
    /*------------------------------------------------------------------------*/
    static string REG_PrevValue(string sExt)
    {
        /* "Prev" + the extension with a capital + "ProgId". */
        return("Prev" + char.ToUpperInvariant(sExt[1]) + sExt.Substring(2)
            + "ProgId");
    }

    /*------------------------------------------------------------------------*/
    /* REG_TypeName:                                                          */
    /*                                                                        */
    /* The file type's name Explorer shows while Voorhees is the default.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sExt : the extension with its dot, lower case.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "JSON File (Voorhees)".                              */
    /*------------------------------------------------------------------------*/
    static string REG_TypeName(string sExt)
    {
        switch (sExt)
        {
            case ".json": return("JSON File (Voorhees)");                     // JSON
            case ".ini":  return("Configuration Settings (Voorhees)");        // INI
            case ".cfg":  return("Klipper Config File (Voorhees)");           // printer.cfg
            case ".conf": return("Config File (Voorhees)");                   // moonraker.conf ...
            default:      return("Registry File (Voorhees)");                 // .reg
        }
    }

    /*------------------------------------------------------------------------*/
    /* REG_ParseExtensions:                                                   */
    /*                                                                        */
    /* The extensions a --set-default / --unset-default list names: comma-    */
    /* separated, with or without dots, any case; each must be one Voorhees   */
    /* registers.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sList  : the list, e.g. "json,.ini".                               */
    /*     sError : set to why it is not valid, else null.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the extensions (".json" ...), or null when invalid. */
    /*------------------------------------------------------------------------*/
    public static List<string> REG_ParseExtensions(string sList, out string sError)
    {
        List<string> lstExt;                             // the extensions
        string sExt;                                     // one of them, normalised

        sError = null;
        lstExt = new List<string>();
        foreach (string sPart in sList.Split(','))
        {
            sExt = sPart.Trim().ToLowerInvariant();
            if (!sExt.StartsWith(".", StringComparison.Ordinal))
            {   /* Without its dot: add it. */
                sExt = "." + sExt;
            }
            if (Array.IndexOf(REG_EXTENSIONS, sExt) < 0)
            {   /* Not one of Voorhees' extensions. */
                sError = "\"" + sPart + "\" is not one of json, ini, cfg, conf, reg";
                return(null);
            }
            if (!lstExt.Contains(sExt))
            {   /* Once each. */
                lstExt.Add(sExt);
            }
        }

        /* Every extension named. */
        return(lstExt);
    }

    /*------------------------------------------------------------------------*/
    /* SHChangeNotify:                                                        */
    /*                                                                        */
    /* shell32.dll, imported.  Tells Explorer that something it caches        */
    /* has changed.  Voorhees sends only REG_SHCNE_ASSOCCHANGED, after        */
    /* changing file associations, so Explorer's menus and icons for          */
    /* .json refresh without signing out.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     wEventId : what changed; REG_SHCNE_ASSOCCHANGED here.              */
    /*     uFlags   : how dwItem1 / dwItem2 are encoded; 0 (unused).          */
    /*     dwItem1  : first item affected; IntPtr.Zero for this event.        */
    /*     dwItem2  : second item affected; IntPtr.Zero for this event.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Explorer is notified; there is no result to check.          */
    /*------------------------------------------------------------------------*/
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void SHChangeNotify(int wEventId, uint uFlags,
        IntPtr dwItem1, IntPtr dwItem2);

    const int REG_SHCNE_ASSOCCHANGED = 0x08000000;       // SHChangeNotify event: associations changed

    /*----------------------------------------------------------------------*/
    /* REG_RegisterContextMenu:                                             */
    /*                                                                      */
    /* Tools > Register Explorer Context Menu.  Does the registration       */
    /* (REG_DoRegister) and tells the user how it went.  On success the     */
    /* message also explains that on Windows 10 and 11 the user's own       */
    /* Default Apps choice still decides what a double click opens.         */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the registry is updated and the user told the outcome.    */
    /*----------------------------------------------------------------------*/
    void REG_RegisterContextMenu()
    {
        string sError;                                   // why registering failed, or null

        sError = REG_DoRegister();
        if (sError != null)
        {   /* Registry write refused: report it. */
            DLG_Message(
                "Failed to register context menu: " + sError,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        /* Say what was done, and how defaults are chosen. */
        DLG_Message(
            "\"Edit with Voorhees\" has been added to the right-click "
            + "menu for .json, .ini, .cfg, .conf and .reg files, and "
            + "Voorhees is listed under Open With for them.\r\n\r\n"
            + "No default program was changed.  To make Voorhees the "
            + "default for some of them, use Tools > Set Voorhees as "
            + "Default.",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /*------------------------------------------------------------------------*/
    /* REG_DoRegister:                                                        */
    /*                                                                        */
    /* The registration itself, shared by the Tools menu and the command      */
    /* line's --register; it shows nothing.  Registers, for the current       */
    /* user and each of REG_EXTENSIONS:                                       */
    /*     o Voorhees' file type (REG_ProgId): name, icon, open command;      */
    /*     o "Edit with Voorhees" on the extension's right-click menu;        */
    /*     o Voorhees in the extension's "Open with" list.                    */
    /* No default is changed (that is REG_DoSetDefault's).  Explorer is then  */
    /* told the associations changed.  The commands registered name this exe  */
    /* where it is now, so registering again after moving it updates them.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null on success, else why the registry refused.           */
    /*------------------------------------------------------------------------*/
    public static string REG_DoRegister()
    {
        string exePath;                                  // this program
        string command;                                  // command line Explorer runs: "exe" "file"
        string progId;                                   // an extension's file type
        RegistryKey key;                                 // key being written

        exePath = Application.ExecutablePath;
        command = "\"" + exePath + "\" \"%1\"";

        try
        {
            foreach (string sExt in REG_EXTENSIONS)
            {
                progId = REG_ProgId(sExt);

                /* The file type: name and open command.  An "edit" verb   */
                /* left by 1.x versions is removed: the verb under         */
                /* SystemFileAssociations replaces it.                     */
                key = Registry.CurrentUser.CreateSubKey(REG_CLASSES + progId);
                key.SetValue("", REG_TypeName(sExt));
                key.DeleteSubKeyTree("shell\\edit", false);
                key.Close();

                /* Icon while Voorhees is the default: the exe's first. */
                key = Registry.CurrentUser.CreateSubKey(
                    REG_CLASSES + progId + "\\DefaultIcon");
                key.SetValue("", exePath + ",0");
                key.Close();

                /* What a double click runs while Voorhees is the default. */
                key = Registry.CurrentUser.CreateSubKey(
                    REG_CLASSES + progId + "\\shell\\open\\command");
                key.SetValue("", command);
                key.Close();

                /* The right-click verb, shown whatever the default is. */
                key = Registry.CurrentUser.CreateSubKey(REG_SFA + sExt
                    + "\\shell\\Voorhees");
                key.SetValue("", "Edit with Voorhees");
                key.SetValue("Icon", exePath + ",0");
                key.Close();

                key = Registry.CurrentUser.CreateSubKey(REG_SFA + sExt
                    + "\\shell\\Voorhees\\command");
                key.SetValue("", command);
                key.Close();

                /* "Open with" entry: an empty REG_NONE value named after the ProgId. */
                key = Registry.CurrentUser.CreateSubKey(
                    REG_CLASSES + sExt + "\\OpenWithProgids");
                key.SetValue(progId, new byte[0], RegistryValueKind.None);
                key.Close();
            }

            /* Make Explorer pick the changes up now. */
            SHChangeNotify(REG_SHCNE_ASSOCCHANGED, 0,
                IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {   /* Registry write refused: hand back why. */
            return(ex.Message);
        }

        /* Registered. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* REG_IsDefault:                                                         */
    /*                                                                        */
    /* Whether Voorhees is an extension's default here (its HKCU class        */
    /* default names Voorhees' file type).  Windows 10 and 11 may still let   */
    /* the user's Default Apps choice win; this is what Voorhees set.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sExt : the extension with its dot, lower case.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it is.                                            */
    /*------------------------------------------------------------------------*/
    public static bool REG_IsDefault(string sExt)
    {
        RegistryKey key;                                 // the extension's class key
        string current;                                  // its default

        key = Registry.CurrentUser.OpenSubKey(REG_CLASSES + sExt);
        if (key == null)
        {   /* Nothing registered for this user. */
            return(false);
        }
        current = key.GetValue("") as string;
        key.Close();

        /* Ours, or not. */
        return(current == REG_ProgId(sExt));
    }

    /*------------------------------------------------------------------------*/
    /* REG_DoSetDefault:                                                      */
    /*                                                                        */
    /* Makes Voorhees an extension's default (registering first, so its file  */
    /* type exists), saving the previous default (REG_PrevValue) unless it is */
    /* already Voorhees.  Shared by the Set as Default dialog and             */
    /* --set-default; it shows nothing.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sExt : the extension with its dot, lower case.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null on success, else why the registry refused.           */
    /*------------------------------------------------------------------------*/
    public static string REG_DoSetDefault(string sExt)
    {
        RegistryKey key;                                 // the extension's class key
        string previous;                                 // its default before
        string sError;                                   // why registering failed

        sError = REG_DoRegister();
        if (sError != null)
        {   /* Could not even register. */
            return(sError);
        }

        try
        {
            key = Registry.CurrentUser.CreateSubKey(REG_CLASSES + sExt);
            previous = key.GetValue("") as string;
            if (previous != REG_ProgId(sExt))
            {   /* Someone else's default (or none): saved for putting back. */
                if (previous == null)
                {   /* None: remembered as empty. */
                    previous = "";
                }
                CFG_SetString(REG_PrevValue(sExt), previous);
            }
            key.SetValue("", REG_ProgId(sExt));
            key.Close();
            SHChangeNotify(REG_SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {   /* Registry write refused. */
            return(ex.Message);
        }

        /* Voorhees is the default. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* REG_DoUnsetDefault:                                                    */
    /*                                                                        */
    /* Gives an extension its previous default back, if Voorhees holds it:    */
    /* the saved one, or none at all when there was none.  Not holding it is  */
    /* not an error.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sExt : the extension with its dot, lower case.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null on success, else why the registry refused.           */
    /*------------------------------------------------------------------------*/
    public static string REG_DoUnsetDefault(string sExt)
    {
        RegistryKey key;                                 // the extension's class key
        string previous;                                 // the saved default

        try
        {
            key = Registry.CurrentUser.OpenSubKey(REG_CLASSES + sExt, true);
            if (key != null)
            {   /* Registered for this user: is the default ours? */
                if ((key.GetValue("") as string) == REG_ProgId(sExt))
                {   /* Ours: put the previous one back, or none. */
                    previous = CFG_GetString(REG_PrevValue(sExt));
                    if (!string.IsNullOrEmpty(previous))
                    {   /* There was one. */
                        key.SetValue("", previous);
                    }
                    else
                    {   /* There was none. */
                        key.DeleteValue("", false);
                    }
                }
                key.Close();
            }
            CFG_SetString(REG_PrevValue(sExt), null);
            SHChangeNotify(REG_SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {   /* Registry change refused. */
            return(ex.Message);
        }

        /* Not Voorhees' any more. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* REG_ShowDefaults:                                                      */
    /*                                                                        */
    /* Tools > Set Voorhees as Default (INI 7): the dialog                    */
    /* (VoorheesDefaultsForm) with one checkbox per extension and what the    */
    /* extension is (REG_DESCRIPTIONS), each ticked when Voorhees is that     */
    /* extension's default now (REG_IsDefault), and the notes on .reg and on  */
    /* Windows 10 / 11.  OK applies the changes: a newly ticked box makes     */
    /* Voorhees the default (REG_DoSetDefault), a newly cleared one puts the  */
    /* previous default back (REG_DoUnsetDefault).  Modal: a short question   */
    /* that must be answered (the global modeless preference allows that).    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the defaults are changed as ticked, and the user told any   */
    /*            failure.                                                    */
    /*------------------------------------------------------------------------*/
    void REG_ShowDefaults()
    {
        VoorheesDefaultsForm dlg;                        // the dialog
        bool[] arrWas;                                   // each extension's default before
        bool[] arrNow;                                   // each one as ticked
        bool bOk;                                        // OK was pressed
        string sError;                                   // a refusal
        int i;

        /* Which extensions open in Voorhees now: those boxes start ticked. */
        arrWas = new bool[REG_EXTENSIONS.Length];
        for (i = 0; i < REG_EXTENSIONS.Length; i++)
        {
            arrWas[i] = REG_IsDefault(REG_EXTENSIONS[i]);
        }

        /* The dialog (VoorheesDefaultsForm); its outcome read before it goes. */
        arrNow = new bool[REG_EXTENSIONS.Length];
        dlg = VoorheesDefaultsForm.Vdf_Create(Font, REG_EXTENSIONS, REG_DESCRIPTIONS, arrWas);
        using (dlg)
        {
            dlg.ShowDialog(this);
            bOk = dlg.Vdf_bSaved;
            for (i = 0; i < REG_EXTENSIONS.Length; i++)
            {
                arrNow[i] = dlg.Vdf_IsTicked(i);
            }
        }

        if (!bOk)
        {   /* Cancelled: nothing changes. */
            return;
        }

        /* Apply what changed, stopping at the first refusal. */
        for (i = 0; i < REG_EXTENSIONS.Length; i++)
        {
            sError = null;
            if (arrNow[i] && !arrWas[i])
            {   /* Newly ticked: Voorhees becomes the default. */
                sError = REG_DoSetDefault(REG_EXTENSIONS[i]);
            }
            else if (!arrNow[i] && arrWas[i])
            {   /* Newly cleared: the previous default comes back. */
                sError = REG_DoUnsetDefault(REG_EXTENSIONS[i]);
            }

            if (sError != null)
            {   /* The registry refused: say so. */
                DLG_Message("Could not change the default for "
                    + REG_EXTENSIONS[i] + ": " + sError, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
        }
    }

    /*----------------------------------------------------------------------*/
    /* REG_UnregisterContextMenu:                                           */
    /*                                                                      */
    /* Tools > Unregister Explorer Context Menu.  Does the removal          */
    /* (REG_DoUnregister) and tells the user how it went.                   */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the registry is updated and the user told the outcome.    */
    /*----------------------------------------------------------------------*/
    void REG_UnregisterContextMenu()
    {
        string sError;                                   // why unregistering failed, or null

        sError = REG_DoUnregister();
        if (sError != null)
        {   /* Registry change refused: report it. */
            DLG_Message(
                "Failed to unregister context menu: " + sError,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        /* Removed. */
        DLG_Message(
            "Explorer context menu unregistered successfully.",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /*----------------------------------------------------------------------*/
    /* REG_DoUnregister:                                                    */
    /*                                                                      */
    /* The removal itself, shared by the Tools menu and the command         */
    /* line's --unregister; it shows nothing.  For each of REG_EXTENSIONS:  */
    /* puts back every default Voorhees holds (REG_DoUnsetDefault), then    */
    /* removes Voorhees' file type, the right-click verb and the "Open      */
    /* with" entry.  Removing what is not there is not an error.            */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     string : null on success, else why the registry refused.         */
    /*----------------------------------------------------------------------*/
    public static string REG_DoUnregister()
    {
        string sError;                                   // why a default could not be put back
        RegistryKey key;                                 // key being edited

        try
        {
            foreach (string sExt in REG_EXTENSIONS)
            {
                /* The default first, while the file type still exists. */
                sError = REG_DoUnsetDefault(sExt);
                if (sError != null)
                {   /* Refused: stop and say why. */
                    return(sError);
                }

                /* The file type and the right-click verb. */
                Registry.CurrentUser.DeleteSubKeyTree(
                    REG_CLASSES + REG_ProgId(sExt), false);
                Registry.CurrentUser.DeleteSubKeyTree(REG_SFA + sExt
                    + "\\shell\\Voorhees", false);

                /* The "Open with" entry. */
                key = Registry.CurrentUser.OpenSubKey(
                    REG_CLASSES + sExt + "\\OpenWithProgids", true);
                if (key != null)
                {   /* The list exists: take Voorhees out of it. */
                    key.DeleteValue(REG_ProgId(sExt), false);
                    key.Close();
                }
            }

            /* Make Explorer pick the changes up now. */
            SHChangeNotify(REG_SHCNE_ASSOCCHANGED, 0,
                IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {   /* Registry change refused: hand back why. */
            return(ex.Message);
        }

        /* Unregistered. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* CFG_ReadColor:                                                         */
    /*                                                                        */
    /* One colour setting: a DWORD 0x00RRGGBB                                 */
    /* under HKCU\Software\Voorhees; a missing or unreadable value gives the  */
    /* default.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     key      : the open settings key.                                  */
    /*     sName    : the value's name (ColorTopName, ColorText ...).         */
    /*     colorDef : the role's default colour.                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : the colour.                                                */
    /*------------------------------------------------------------------------*/
    static Color CFG_ReadColor(RegistryKey key, string sName, Color colorDef)
    {
        object oValue;                                   // the stored value, or null

        oValue = key.GetValue(sName);
        if (!(oValue is int))
        {   /* Not set (or not a DWORD): the default. */
            return(colorDef);
        }

        /* Opaque, from its red, green and blue bytes. */
        return(Color.FromArgb(255, Color.FromArgb((int)oValue)));
    }

    /*------------------------------------------------------------------------*/
    /* Colour settings' value names, in the                                   */
    /* tree's role order: VoorheesTreeView.VTV_NAME_ and VoorheesFormat.      */
    /* VF_ROLE_.                                                              */
    /*------------------------------------------------------------------------*/

    static readonly string[] CFG_NAMECOLORS = new string[]
    {
        "ColorTopName",                                  // VTV_NAME_TOP
        "ColorNestedName",                               // VTV_NAME_NESTED
        "ColorEntryName"                                 // VTV_NAME_ENTRY
    };

    static readonly string[] CFG_VALUECOLORS = new string[]
    {
        "ColorText",                                     // VF_ROLE_TEXT
        "ColorNumber",                                   // VF_ROLE_NUMBER
        "ColorBoolean",                                  // VF_ROLE_BOOLEAN
        "ColorNull",                                     // VF_ROLE_NULL
        "ColorBinary"                                    // VF_ROLE_BINARY
    };

    /*------------------------------------------------------------------------*/
    /* CFG_LoadColors:                                                        */
    /*                                                                        */
    /* Reads every role's colour into the tree's colour arrays, each missing  */
    /* one taking its default (CFG_ReadColor).                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     key : the open settings key.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : tree.Vtv_arrNameColors and Vtv_arrValueColors are set.      */
    /*------------------------------------------------------------------------*/
    void CFG_LoadColors(RegistryKey key)
    {
        Color[] arrNames;                                // the name roles' defaults
        Color[] arrValues;                               // the value roles' defaults
        int i;

        arrNames = VoorheesTreeView.Vtv_DefaultNameColors();
        arrValues = VoorheesTreeView.Vtv_DefaultValueColors();
        for (i = 0; i < CFG_NAMECOLORS.Length; i++)
        {
            tree.Vtv_arrNameColors[i] = CFG_ReadColor(key, CFG_NAMECOLORS[i], arrNames[i]);
        }
        for (i = 0; i < CFG_VALUECOLORS.Length; i++)
        {
            tree.Vtv_arrValueColors[i] = CFG_ReadColor(key, CFG_VALUECOLORS[i], arrValues[i]);
        }
    }

    /*------------------------------------------------------------------------*/
    /* CFG_WriteColor:                                                        */
    /*                                                                        */
    /* Stores one colour setting as a DWORD 0x00RRGGBB (CFG_ReadColor reads   */
    /* it back).                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     key   : the settings key, open for writing.                        */
    /*     sName : the value's name.                                          */
    /*     color : the colour.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value is written.                                       */
    /*------------------------------------------------------------------------*/
    static void CFG_WriteColor(RegistryKey key, string sName, Color color)
    {
        /* Red, green and blue; no alpha. */
        key.SetValue(sName, color.ToArgb() & 0x00FFFFFF, RegistryValueKind.DWord);
    }

    /*------------------------------------------------------------------------*/
    /* CFG_StoreColor:                                                        */
    /*                                                                        */
    /* Keeps one colour setting: a colour that is its role's default is       */
    /* stored as NO value (a missing value means                              */
    /* the default -- so Reset leaves nothing behind, and a default that      */
    /* follows the system's text colour keeps following it); any other is     */
    /* written (CFG_WriteColor).  Colours compare by their RGB values.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     key      : the settings key, open for writing.                     */
    /*     sName    : the value's name.                                       */
    /*     color    : the colour.                                             */
    /*     colorDef : the role's default.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value is written or removed.                            */
    /*------------------------------------------------------------------------*/
    static void CFG_StoreColor(RegistryKey key, string sName, Color color,
        Color colorDef)
    {
        if (color.ToArgb() == colorDef.ToArgb())
        {   /* The default: no value (removing a missing one is not an error). */
            key.DeleteValue(sName, false);
            return;
        }

        /* A colour of the user's own. */
        CFG_WriteColor(key, sName, color);
    }

    /*------------------------------------------------------------------------*/
    /* CFG_SaveColors:                                                        */
    /*                                                                        */
    /* Stores every role's colour from the tree's tables (CFG_StoreColor,     */
    /* names CFG_NAMECOLORS / CFG_VALUECOLORS), for CFG_LoadColors next time; */
    /* called when the Colors dialog's OK changes them.  A harness run leaves */
    /* the registry alone (CFG_HookSkip).  Failure is ignored, as CFG_Save    */
    /* ignores it: the colours still apply for this session.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : HKCU\Software\Voorhees holds the colours.                   */
    /*------------------------------------------------------------------------*/
    void CFG_SaveColors()
    {
        RegistryKey key;                                 // HKCU\Software\Voorhees
        Color[] arrNames;                                // the name roles' defaults
        Color[] arrValues;                               // the value roles' defaults
        bool bSkip;                                      // the test harness keeps the settings untouched
        int i;

        bSkip = false;
        CFG_HookSkip(ref bSkip);
        if (bSkip)
        {   /* A harness run: leave the user's settings alone. */
            return;
        }

        try
        {
            arrNames = VoorheesTreeView.Vtv_DefaultNameColors();
            arrValues = VoorheesTreeView.Vtv_DefaultValueColors();
            key = Registry.CurrentUser.CreateSubKey(CFG_REGPATH);
            for (i = 0; i < CFG_NAMECOLORS.Length; i++)
            {
                CFG_StoreColor(key, CFG_NAMECOLORS[i], tree.Vtv_arrNameColors[i], arrNames[i]);
            }
            for (i = 0; i < CFG_VALUECOLORS.Length; i++)
            {
                CFG_StoreColor(key, CFG_VALUECOLORS[i], tree.Vtv_arrValueColors[i], arrValues[i]);
            }
            key.Close();
        }
        catch
        {   /* Registry not writable: not worth bothering the user about. */
        }
    }

    /*------------------------------------------------------------------------*/
    /* HELP: the About box                                                    */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* HELP_ShowAbout:                                                        */
    /*                                                                        */
    /* Help > About: shows the About box until it is closed.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : returns when the About box closes.                          */
    /*------------------------------------------------------------------------*/
    void HELP_ShowAbout()
    {
        AboutForm dlg;                                   // the About box

        dlg = new AboutForm();
        using (dlg)
        {
            dlg.ShowDialog(this);
        }
    }

    /*------------------------------------------------------------------------*/
    /* OnFormClosing:                                                         */
    /*                                                                        */
    /* The window is closing.  FILE_ConfirmDiscard runs every time: it        */
    /* settles changes typed into the panel as well as unsaved document       */
    /* changes.  If the user cancels, the window stays open.  Otherwise       */
    /* the settings are saved and the Find window, which has no owner, is     */
    /* closed too.                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     e : set e.Cancel to keep the window open.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the close goes ahead or is cancelled.                       */
    /*------------------------------------------------------------------------*/
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!FILE_ConfirmDiscard())
        {   /* User cancelled (or a save failed): stay open. */
            e.Cancel = true;
            return;
        }

        /* Remember the window for next time. */
        CFG_Save();

        if (findForm != null && !findForm.IsDisposed)
        {   /* The Find window has no owner, so it would outlive the editor: close it. */
            findForm.Close();
        }

        base.OnFormClosing(e);
    }

    /*------------------------------------------------------------------------*/
    /* DND: opening a file dropped on the window                              */
    /*------------------------------------------------------------------------*/

    /*----------------------------------------------------------------------*/
    /* DND_Enable:                                                          */
    /*                                                                      */
    /* Lets a control accept a dropped file.                                */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     c : the control.                                                 */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the control accepts drops and is wired to the handlers.   */
    /*----------------------------------------------------------------------*/
    void DND_Enable(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += DND_DragEnter;
        c.DragDrop += DND_DragDrop;
    }

    /*------------------------------------------------------------------------*/
    /* DND_DragEnter:                                                         */
    /*                                                                        */
    /* Something is being dragged over the window: accept it (copy            */
    /* cursor) only if it is one or more files.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sender : control under the mouse.                                  */
    /*     e      : the dragged data; e.Effect shows acceptance.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : e.Effect is set.                                            */
    /*------------------------------------------------------------------------*/
    void DND_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {   /* Files: accept. */
            e.Effect = DragDropEffects.Copy;
        }
        else
        {   /* Text or anything else: refuse. */
            e.Effect = DragDropEffects.None;
        }
    }

    /*------------------------------------------------------------------------*/
    /* DND_DragDrop:                                                          */
    /*                                                                        */
    /* Files were dropped: open the first one.  The opening runs after        */
    /* the drop has finished, because the save prompt is modal, and           */
    /* showing it inside the drop event leaves Explorer frozen mid-drag       */
    /* until it is answered.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sender : control the files were dropped on.                        */
    /*     e      : the dropped data.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the file opening is queued.                                 */
    /*------------------------------------------------------------------------*/
    void DND_DragDrop(object sender, DragEventArgs e)
    {
        string[] files;                                  // dropped file paths
        string path;                                     // the one to open

        files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0)
        {   /* Not files after all: nothing to open. */
            return;
        }

        path = files[0];
        BeginInvoke((MethodInvoker)delegate
        {
            if (FILE_ConfirmDiscard())
            {   /* Current document saved or discarded: open the dropped file. */
                FILE_LoadFromPath(path, VoorheesFormat.VF_DETECT);
            }
        });
    }
}

/*----------------------------------------------------------------------------*/
/* FindReplaceForm: the Find / Find + Replace window                          */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* FindReplaceForm                                                            */
/*                                                                            */
/* A small modeless tool window.  It holds no search logic of its own:        */
/* every button calls the MainForm SEARCH_ functions, which work on the       */
/* document and the tree.  The same window serves Find (replace controls      */
/* hidden) and Find + Replace; SHOW_SetMode switches between them.            */
/*----------------------------------------------------------------------------*/
class FindReplaceForm : Form
{
    /*----------------------------------------------------------------------*/
    /* Spacing, pixels, and the narrowest text box, in characters.          */
    /* Everything else is measured from the fonts and captions.             */
    /*----------------------------------------------------------------------*/

    const int FRF_PAD = 12;                              // space round the content
    const int FRF_GAP = 6;                               // space between rows and between buttons
    const int FRF_MINCHARS = 36;                         // text boxes are at least this many average characters wide

    MainForm owner;                                      // editor whose document is searched
    Label lblFind;                                       // "Find:" caption
    TextBox txtFind;                                     // text to look for
    TextBox txtReplace;                                  // replacement text
    Label lblReplace;                                    // "Replace:" caption, hidden in Find mode
    CheckBox chkMatchCase;                               // exact-case matching
    CheckBox chkSearchKeys;                              // look in object member keys
    CheckBox chkSearchValues;                            // look in plain values and comments
    Button btnFindNext;                                  // select the next match
    Button btnReplace;                                   // replace in the current match, then find the next
    Button btnReplaceAll;                                // replace everywhere as one undo step
    Button btnClose;                                     // close the window

    /*------------------------------------------------------------------------*/
    /* FindReplaceForm:                                                       */
    /*                                                                        */
    /* Builds the window: Find and Replace boxes, Match case, Search keys     */
    /* and Search values options (both searches on by default; values         */
    /* include comments) and the four buttons.  Enter is Find Next and        */
    /* Escape is Close.  No control is given a position here: the window      */
    /* is sized to its measured content (FRF_PreferredClientSize) and         */
    /* FRF_Layout places everything, here and on every resize.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     mainForm    : the editor to search.                                */
    /*     showReplace : true to open in Find + Replace layout.               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     FindReplaceForm : the window, not yet shown.                       */
    /*------------------------------------------------------------------------*/
    public FindReplaceForm(MainForm mainForm, bool showReplace)
    {
        owner = mainForm;

        Text = "Find";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        /* Find row. */
        lblFind = new Label();
        lblFind.Text = "Find:";
        lblFind.AutoSize = true;
        Controls.Add(lblFind);

        txtFind = new TextBox();
        Controls.Add(txtFind);

        /* Replace row (hidden in Find mode). */
        lblReplace = new Label();
        lblReplace.Text = "Replace:";
        lblReplace.AutoSize = true;
        Controls.Add(lblReplace);

        txtReplace = new TextBox();
        Controls.Add(txtReplace);

        /* Options: Match case on its own row, off by default. */
        chkMatchCase = new CheckBox();
        chkMatchCase.Text = "Match case";
        chkMatchCase.AutoSize = true;
        Controls.Add(chkMatchCase);

        /* Then what to search, side by side, both on by default. */
        chkSearchKeys = new CheckBox();
        chkSearchKeys.Text = "Search keys";
        chkSearchKeys.AutoSize = true;
        chkSearchKeys.Checked = true;
        Controls.Add(chkSearchKeys);

        chkSearchValues = new CheckBox();
        chkSearchValues.Text = "Search values";
        chkSearchValues.AutoSize = true;
        chkSearchValues.Checked = true;
        Controls.Add(chkSearchValues);

        /* Buttons, one row, left to right.  Find Next first: it is   */
        /* the Enter key's button.                                    */
        btnFindNext = new Button();
        btnFindNext.Text = "Find Next";
        btnFindNext.Click += delegate { DO_FindNext(); };
        Controls.Add(btnFindNext);

        /* Replace and Replace All, hidden in Find-only mode. */
        btnReplace = new Button();
        btnReplace.Text = "Replace";
        btnReplace.Click += delegate { DO_Replace(); };
        Controls.Add(btnReplace);

        /* Replace All: everywhere at once, as one undo step. */
        btnReplaceAll = new Button();
        btnReplaceAll.Text = "Replace All";
        btnReplaceAll.Click += delegate { DO_ReplaceAll(); };
        Controls.Add(btnReplaceAll);

        /* Close, last: it is the Escape key's button. */
        btnClose = new Button();
        btnClose.Text = "Close";
        btnClose.Click += delegate { Close(); };
        Controls.Add(btnClose);

        AcceptButton = btnFindNext;
        CancelButton = btnClose;

        /* Size to the content, place everything, and place it again   */
        /* whenever the size changes.                                  */
        ClientSize = FRF_PreferredClientSize();
        FRF_Layout();
        Resize += delegate { FRF_Layout(); };

        SHOW_SetMode(showReplace);
    }

    /*----------------------------------------------------------------------*/
    /* FRF_ButtonSize:                                                      */
    /*                                                                      */
    /* The one size all four buttons share, from the longest caption        */
    /* (MainForm.DLG_ButtonSize).                                           */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     Size : the size of each button.                                  */
    /*----------------------------------------------------------------------*/
    Size FRF_ButtonSize()
    {
        /* Measured over all four captions. */
        return(MainForm.DLG_ButtonSize(new Button[]
        {
            btnFindNext, btnReplace, btnReplaceAll, btnClose
        }));
    }

    /*------------------------------------------------------------------------*/
    /* FRF_PreferredClientSize:                                               */
    /*                                                                        */
    /* The client size the window needs.  Width: the wider of the button      */
    /* row and the caption column plus a text box of FRF_MINCHARS             */
    /* characters (or the two search options side by side, if wider).         */
    /* Height: the Find, Replace, Match case, search-option and button        */
    /* rows with FRF_GAP between them and FRF_PAD round them.  The Replace    */
    /* row is kept in Find mode too, so switching modes does not resize       */
    /* the window.                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Size : the client size to give the window.                         */
    /*------------------------------------------------------------------------*/
    Size FRF_PreferredClientSize()
    {
        Size szButton;                                   // size of each button
        int iCaptionWidth;                               // width of the caption column
        int iFieldWidth;                                 // narrowest text box
        int iOptionsWidth;                               // the two search options side by side
        int iWidth;                                      // content width
        int iHeight;                                     // content height

        szButton = FRF_ButtonSize();
        iCaptionWidth = Math.Max(lblFind.PreferredSize.Width,
            lblReplace.PreferredSize.Width);
        iFieldWidth = TextRenderer.MeasureText(new string('x', FRF_MINCHARS),
            txtFind.Font).Width;
        iOptionsWidth = chkSearchKeys.PreferredSize.Width + FRF_PAD * 2
            + chkSearchValues.PreferredSize.Width;

        /* The widest row decides the width. */
        iWidth = Math.Max(szButton.Width * 4 + FRF_GAP * 3,
            iCaptionWidth + FRF_GAP + Math.Max(iFieldWidth, iOptionsWidth));

        /* Two text rows, the two option rows, and the buttons. */
        iHeight = txtFind.Height + FRF_GAP
            + txtReplace.Height + FRF_GAP
            + chkMatchCase.PreferredSize.Height + FRF_GAP
            + chkSearchKeys.PreferredSize.Height + FRF_PAD
            + szButton.Height;

        /* Content plus padding all round. */
        return(new Size(iWidth + FRF_PAD * 2, iHeight + FRF_PAD * 2));
    }

    /*----------------------------------------------------------------------*/
    /* FRF_Layout:                                                          */
    /*                                                                      */
    /* Places every control from the measured sizes and the current client  */
    /* width.  Captions in a column on the left, each centred on its text   */
    /* box; text boxes and options start right of the widest caption, the   */
    /* boxes stretching to the right edge; Search values sits beside        */
    /* Search keys; the four buttons share one size in a row along the      */
    /* bottom, from the left.                                               */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the controls are moved and sized.                         */
    /*----------------------------------------------------------------------*/
    void FRF_Layout()
    {
        Size szButton;                                   // size of each button
        int iFieldLeft;                                  // x of the text boxes and options
        int iFieldWidth;                                 // width of the text boxes
        int y;                                           // top of the row being placed

        if (btnClose == null)
        {   /* A resize raised while the controls are still being built. */
            return;
        }

        szButton = FRF_ButtonSize();
        iFieldLeft = FRF_PAD + Math.Max(lblFind.PreferredSize.Width,
            lblReplace.PreferredSize.Width) + FRF_GAP;
        iFieldWidth = Math.Max(1, ClientSize.Width - iFieldLeft - FRF_PAD);

        /* Find row. */
        y = FRF_PAD;
        txtFind.SetBounds(iFieldLeft, y, iFieldWidth, txtFind.Height);
        lblFind.Location = new Point(FRF_PAD,
            y + (txtFind.Height - lblFind.PreferredSize.Height) / 2);
        y += txtFind.Height + FRF_GAP;

        /* Replace row. */
        txtReplace.SetBounds(iFieldLeft, y, iFieldWidth, txtReplace.Height);
        lblReplace.Location = new Point(FRF_PAD,
            y + (txtReplace.Height - lblReplace.PreferredSize.Height) / 2);
        y += txtReplace.Height + FRF_GAP;

        /* Match case. */
        chkMatchCase.Location = new Point(iFieldLeft, y);
        y += chkMatchCase.PreferredSize.Height + FRF_GAP;

        /* Search keys, then Search values beside it. */
        chkSearchKeys.Location = new Point(iFieldLeft, y);
        chkSearchValues.Location = new Point(
            iFieldLeft + chkSearchKeys.PreferredSize.Width + FRF_PAD * 2, y);
        y += chkSearchKeys.PreferredSize.Height + FRF_PAD;

        /* Buttons, left to right, one size. */
        btnFindNext.SetBounds(FRF_PAD, y, szButton.Width, szButton.Height);
        btnReplace.SetBounds(btnFindNext.Right + FRF_GAP, y,
            szButton.Width, szButton.Height);
        btnReplaceAll.SetBounds(btnReplace.Right + FRF_GAP, y,
            szButton.Width, szButton.Height);
        btnClose.SetBounds(btnReplaceAll.Right + FRF_GAP, y,
            szButton.Width, szButton.Height);
    }

    /*------------------------------------------------------------------------*/
    /* SHOW_SetMode:                                                          */
    /*                                                                        */
    /* Switches between Find (replace controls hidden) and Find +             */
    /* Replace.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     showReplace : true for Find + Replace.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the title and the replace controls are updated.             */
    /*------------------------------------------------------------------------*/
    public void SHOW_SetMode(bool showReplace)
    {
        if (showReplace)
        {   /* Find + Replace. */
            Text = "Find + Replace";
        }
        else
        {   /* Find only. */
            Text = "Find";
        }

        lblReplace.Visible = showReplace;
        txtReplace.Visible = showReplace;
        btnReplace.Visible = showReplace;
        btnReplaceAll.Visible = showReplace;
    }

    /*------------------------------------------------------------------------*/
    /* DO_FindNext:                                                           */
    /*                                                                        */
    /* Find Next: asks the editor for the next match, and says so when        */
    /* there is none.  Nothing is said when the user cancelled at the         */
    /* editor's unsaved-panel question.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the editor's selection may move.                            */
    /*------------------------------------------------------------------------*/
    void DO_FindNext()
    {
        int result;                                      // 1 found, 0 none, negative = cancelled

        result = owner.SEARCH_FindNext(txtFind.Text,
            chkMatchCase.Checked,
            chkSearchKeys.Checked,
            chkSearchValues.Checked);

        if (result == 0)
        {   /* Searched everything and found nothing. */
            MessageBox.Show("No match found.", "Voorhees",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /*----------------------------------------------------------------------*/
    /* DO_Replace:                                                          */
    /*                                                                      */
    /* Replace: replaces in the selected entry (if it matches), then moves  */
    /* on to the next match.  On an entry that does not match, the first    */
    /* click just finds the next match and the second replaces it.          */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     None.                                                            */
    /*                                                                      */
    /* Returns:                                                             */
    /*     void : the document may change and the selection moves.          */
    /*----------------------------------------------------------------------*/
    void DO_Replace()
    {
        owner.SEARCH_ReplaceCurrent(txtFind.Text, txtReplace.Text,
            chkMatchCase.Checked, chkSearchKeys.Checked,
            chkSearchValues.Checked);

        /* On to the next match. */
        DO_FindNext();
    }

    /*------------------------------------------------------------------------*/
    /* DO_ReplaceAll:                                                         */
    /*                                                                        */
    /* Replace All: replaces everywhere and reports how many were             */
    /* replaced, and how many matches were skipped and why.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document may change (one undo step).                    */
    /*------------------------------------------------------------------------*/
    void DO_ReplaceAll()
    {
        int count;                                       // replacements made
        int skipped;                                     // matches refused
        string message;                                  // report shown afterwards

        count = owner.SEARCH_ReplaceAll(txtFind.Text,
            txtReplace.Text, chkMatchCase.Checked,
            chkSearchKeys.Checked, chkSearchValues.Checked, out skipped);
        if (count < 0)
        {   /* Cancelled at the editor's unsaved-panel question: no report. */
            return;
        }

        message = count.ToString() + " replacement(s) made.";
        if (skipped > 0)
        {   /* Some matches were refused: explain. */
            message += "\r\n\r\n" + skipped.ToString() + " match(es) "
                + "skipped: the result would have been an invalid "
                + "number/boolean, a key that's empty or already "
                + "used in its object, or a comment that can't hold the "
                + "text (or holds several comments).";
        }

        MessageBox.Show(message, "Voorhees", MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /*------------------------------------------------------------------------*/
    /* OnKeyDown:                                                             */
    /*                                                                        */
    /* Escape closes the window from any control, including when focus        */
    /* is in a text box (KeyPreview routes keys here first).                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     e : the key pressed.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the window may close.                                       */
    /*------------------------------------------------------------------------*/
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {   /* Escape: close. */
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesDefaultsForm: the Set Voorhees as Default dialog                   */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* VoorheesDefaultsForm                                                       */
/*                                                                            */
/* Tools > Set Voorhees as Default..., laid out as a Windows settings         */
/* dialog:                                                                    */
/*     o a white header band: a bold title and one line saying what ticking   */
/*       does, an etched line under it;                                       */
/*     o one row per extension: its checkbox (".json") and, in a column of    */
/*       their own, what it is ("JSON and JSONC documents") and, greyed, "now */
/*       opens in Voorhees" where that is so;                                 */
/*     o "Select all" and "Clear all" links;                                  */
/*     o the notes, each beside its standard small icon: a warning for .reg   */
/*       (double-clicking one would open it here instead of merging it), and  */
/*       information on Windows 10 / 11 (a default chosen in Default apps     */
/*       still wins); wrapped to the dialog's width;                          */
/*     o an etched line and OK / Cancel at the bottom right.                  */
/* It applies nothing itself: the editor reads the ticks after OK (Vdf_bSaved */
/* a manual flag, not DialogResult, which behaves differently for modal and   */
/* modeless windows).  Laid out from                                          */
/* measured sizes, again on every resize (Vdf_Layout).  Built by Vdf_Create   */
/* alone, so the test harness can check and picture it with made-up ticks,    */
/* never touching the registry.                                               */
/*----------------------------------------------------------------------------*/
class VoorheesDefaultsForm : Form
{
    public bool Vdf_bSaved;                              // OK was pressed
    Panel Vdf_pnlHeader;                                 // the white header band
    Label Vdf_lblTitle;                                  // its bold title
    Label Vdf_lblIntro;                                  // its line of explanation
    Label Vdf_lnTop;                                     // the etched line under the header
    List<CheckBox> Vdf_lstChecks;                        // one per extension
    List<Label> Vdf_lstDescs;                            // what each extension is
    List<Label> Vdf_lstNows;                             // "now opens in Voorhees" per row, null where not so
    LinkLabel Vdf_lnkAll;                                // Select all
    LinkLabel Vdf_lnkNone;                               // Clear all
    PictureBox Vdf_picWarn;                              // the .reg note's warning icon
    Label Vdf_lblRegNote;                                // the .reg note
    PictureBox Vdf_picInfo;                              // the Windows note's information icon
    Label Vdf_lblWinNote;                                // the Windows note
    Label Vdf_lnBottom;                                  // the etched line above the buttons
    Button Vdf_btnOk;                                    // apply the ticks
    Button Vdf_btnCancel;                                // change nothing

    /*------------------------------------------------------------------------*/
    /* Vdf_Create:                                                            */
    /*                                                                        */
    /* Builds the dialog, not shown.                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     font          : the editor's font (the dialog uses it; the title   */
    /*                     is a bold, larger copy).                           */
    /*     arrExtensions : the extensions (".json" ...).                      */
    /*     arrDescs      : what each is, in the same order.                   */
    /*     arrNow        : whether each opens in Voorhees now (its box starts */
    /*                     ticked, with the grey "now" note).                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDefaultsForm : the dialog, laid out.                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesDefaultsForm Vdf_Create(Font font, string[] arrExtensions,
        string[] arrDescs, bool[] arrNow)
    {
        VoorheesDefaultsForm dlg;                        // the new dialog
        CheckBox chk;                                    // one row's checkbox
        Label lbl;                                       // one row's labels
        Font fontTitle;                                  // the title's font
        Bitmap bmpWarn;                                  // the warning icon, small
        Bitmap bmpInfo;                                  // the information icon, small
        int i;

        /* The window: a fixed dialog centred on the editor. */
        dlg = new VoorheesDefaultsForm();
        dlg.Text = "Set Voorhees as Default";
        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
        dlg.MaximizeBox = false;
        dlg.MinimizeBox = false;
        dlg.ShowInTaskbar = false;
        dlg.StartPosition = FormStartPosition.CenterParent;
        dlg.Font = font;
        dlg.Vdf_bSaved = false;

        /* The header band: white, a bold title, a line of explanation (both    */
        /* children of the band).  The title's font is the dialog's, bold and   */
        /* a third larger; it goes when the dialog does.                        */
        dlg.Vdf_pnlHeader = new Panel();
        dlg.Vdf_pnlHeader.BackColor = SystemColors.Window;
        dlg.Controls.Add(dlg.Vdf_pnlHeader);
        fontTitle = new Font(font.FontFamily, font.SizeInPoints * 4f / 3f, FontStyle.Bold);
        dlg.Disposed += delegate { fontTitle.Dispose(); };
        dlg.Vdf_lblTitle = Vdf_NewLabel(dlg.Vdf_pnlHeader, "Open these file types with Voorhees");
        dlg.Vdf_lblTitle.Font = fontTitle;
        dlg.Vdf_lblIntro = Vdf_NewLabel(dlg.Vdf_pnlHeader,
            "Double-clicking a file of a ticked type opens it in Voorhees.");
        dlg.Vdf_lnTop = MainForm.DLG_NewEtchedLine(dlg);

        /* One row per extension. */
        dlg.Vdf_lstChecks = new List<CheckBox>();
        dlg.Vdf_lstDescs = new List<Label>();
        dlg.Vdf_lstNows = new List<Label>();
        for (i = 0; i < arrExtensions.Length; i++)
        {
            chk = new CheckBox();
            chk.AutoSize = false;
            chk.Text = arrExtensions[i];
            chk.Checked = arrNow[i];
            dlg.Controls.Add(chk);
            dlg.Vdf_lstChecks.Add(chk);

            dlg.Vdf_lstDescs.Add(Vdf_NewLabel(dlg, arrDescs[i]));

            lbl = null;
            if (arrNow[i])
            {   /* Already Voorhees': say so, quietly (other rows have no such label). */
                lbl = Vdf_NewLabel(dlg, "now opens in Voorhees");
                lbl.ForeColor = SystemColors.GrayText;
            }
            dlg.Vdf_lstNows.Add(lbl);
        }

        /* Select all / Clear all. */
        dlg.Vdf_lnkAll = Vdf_NewLink(dlg, "Select all");
        dlg.Vdf_lnkAll.LinkClicked += delegate { dlg.Vdf_SetAll(true); };
        dlg.Vdf_lnkNone = Vdf_NewLink(dlg, "Clear all");
        dlg.Vdf_lnkNone.LinkClicked += delegate { dlg.Vdf_SetAll(false); };

        /* The notes, each with its standard icon at the small icon size    */
        /* (MainForm.DLG_SmallIconBitmap; the pictures go when the dialog   */
        /* does).                                                           */
        bmpWarn = MainForm.DLG_SmallIconBitmap(SystemIcons.Warning);
        bmpInfo = MainForm.DLG_SmallIconBitmap(SystemIcons.Information);
        dlg.Disposed += delegate
        {
            bmpWarn.Dispose();
            bmpInfo.Dispose();
        };
        dlg.Vdf_picWarn = Vdf_NewIcon(dlg, bmpWarn);
        dlg.Vdf_lblRegNote = Vdf_NewLabel(dlg, "With .reg ticked, double-clicking a "
            + "registry file opens it here for editing instead of merging it into the "
            + "registry.");
        dlg.Vdf_picInfo = Vdf_NewIcon(dlg, bmpInfo);
        dlg.Vdf_lblWinNote = Vdf_NewLabel(dlg, "On Windows 10 and 11 a default chosen "
            + "in Settings > Default apps still wins: there, use Open with > Choose "
            + "another app > Always.");

        /* The footer: a line, then OK and Cancel. */
        dlg.Vdf_lnBottom = MainForm.DLG_NewEtchedLine(dlg);
        dlg.Vdf_btnOk = new Button();
        dlg.Vdf_btnOk.Text = "OK";
        dlg.Vdf_btnOk.Click += delegate { dlg.Vdf_Accept(); };
        dlg.Controls.Add(dlg.Vdf_btnOk);
        dlg.Vdf_btnCancel = new Button();
        dlg.Vdf_btnCancel.Text = "Cancel";
        dlg.Vdf_btnCancel.Click += delegate { dlg.Vdf_Cancel(); };
        dlg.Controls.Add(dlg.Vdf_btnCancel);
        dlg.AcceptButton = dlg.Vdf_btnOk;
        dlg.CancelButton = dlg.Vdf_btnCancel;

        /* Placed from measured sizes, now and on every resize. */
        dlg.Resize += delegate { dlg.Vdf_Layout(); };
        dlg.Vdf_Layout();

        /* Ready to show. */
        return(dlg);
    }

    /*------------------------------------------------------------------------*/
    /* Vdf_NewLabel:                                                          */
    /*                                                                        */
    /* A label, sized by Vdf_Layout (not AutoSize: its size is not final      */
    /* until Windows lays the dialog out).                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     parent : the control it goes in (the dialog or the header band).   */
    /*     sText  : its text.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Label : the label, added.                                          */
    /*------------------------------------------------------------------------*/
    static Label Vdf_NewLabel(Control parent, string sText)
    {
        Label lbl;                                       // the new label

        lbl = new Label();
        lbl.AutoSize = false;
        lbl.Text = sText;
        parent.Controls.Add(lbl);

        /* Placed later. */
        return(lbl);
    }

    /*------------------------------------------------------------------------*/
    /* Vdf_NewLink:                                                           */
    /*                                                                        */
    /* A link (Select all, Clear all), sized by Vdf_Layout.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     dlg   : the dialog.                                                */
    /*     sText : its text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     LinkLabel : the link, added.                                       */
    /*------------------------------------------------------------------------*/
    static LinkLabel Vdf_NewLink(VoorheesDefaultsForm dlg, string sText)
    {
        LinkLabel lnk;                                   // the new link

        lnk = new LinkLabel();
        lnk.AutoSize = false;
        lnk.Text = sText;
        dlg.Controls.Add(lnk);

        /* Placed later. */
        return(lnk);
    }

    /*------------------------------------------------------------------------*/
    /* Vdf_NewIcon:                                                           */
    /*                                                                        */
    /* A small icon beside a note, at the system's small icon size.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     dlg : the dialog.                                                  */
    /*     bmp : the icon's picture (the dialog disposes of it).              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     PictureBox : the icon, added.                                      */
    /*------------------------------------------------------------------------*/
    static PictureBox Vdf_NewIcon(VoorheesDefaultsForm dlg, Bitmap bmp)
    {
        PictureBox pic;                                  // the new icon

        pic = new PictureBox();
        pic.Image = bmp;
        pic.SizeMode = PictureBoxSizeMode.Zoom;
        pic.Size = SystemInformation.SmallIconSize;
        dlg.Controls.Add(pic);

        /* Placed later. */
        return(pic);
    }

    /*------------------------------------------------------------------------*/
    /* Vdf_IsTicked:                                                          */
    /*                                                                        */
    /* Whether an extension's box is ticked.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     i : the extension's row.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when ticked.                                           */
    /*------------------------------------------------------------------------*/
    public bool Vdf_IsTicked(int i)
    {
        /* Its box. */
        return(Vdf_lstChecks[i].Checked);
    }

    /*------------------------------------------------------------------------*/
    /* Vdf_SetAll:                                                            */
    /*                                                                        */
    /* Select all / Clear all: every box ticked or cleared.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bTicked : true to tick them all.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : every box is set.                                           */
    /*------------------------------------------------------------------------*/
    public void Vdf_SetAll(bool bTicked)
    {
        int i;

        for (i = 0; i < Vdf_lstChecks.Count; i++)
        {
            Vdf_lstChecks[i].Checked = bTicked;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vdf_Accept:                                                            */
    /*                                                                        */
    /* OK: the ticks are to be applied (Vdf_bSaved) and the dialog closes.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dialog is closing.                                      */
    /*------------------------------------------------------------------------*/
    public void Vdf_Accept()
    {
        /* Kept: the editor reads the flag and the ticks after closing. */
        Vdf_bSaved = true;
        Close();
    }

    /*------------------------------------------------------------------------*/
    /* Vdf_Cancel:                                                            */
    /*                                                                        */
    /* Cancel: nothing is applied and the dialog closes.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dialog is closing.                                      */
    /*------------------------------------------------------------------------*/
    public void Vdf_Cancel()
    {
        /* Closed with the flag still false. */
        Close();
    }

    /*------------------------------------------------------------------------*/
    /* Vdf_Layout:                                                            */
    /*                                                                        */
    /* Places everything from measured sizes, the font's height (iPad) giving */
    /* margins and spacing.  First the content width: the widest of the       */
    /* header's two lines, the rows (checkbox column, description column,     */
    /* "now" column), the links, the two buttons, and a floor of 26 font      */
    /* heights so the notes wrap to a comfortable measure.  Then, top down:   */
    /* the header band across the whole width with its title and line, the    */
    /* etched line, the rows (each column as wide as its widest entry, every  */
    /* label centred on its checkbox), the links, the notes (icon at the      */
    /* margin, text beside it wrapped to the remaining width), the etched     */
    /* line, OK and Cancel at the bottom right; the dialog sized to hold it.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are placed and the client size set.            */
    /*------------------------------------------------------------------------*/
    public void Vdf_Layout()
    {
        int iPad;                                        // margin and spacing, from the font
        int iCheckW;                                     // the checkbox column's width
        int iDescW;                                      // the description column's width
        int iNowW;                                       // the "now" column's width
        int iRowH;                                       // a row's height
        int iContent;                                    // the content's width
        int iNoteX;                                      // where a note's text starts
        Size szButton;                                   // OK's and Cancel's size
        int y;                                           // the next row's top
        int i;

        iPad = Font.Height;

        /* The columns, each as wide as its widest entry. */
        iCheckW = 0;
        iDescW = 0;
        iNowW = 0;
        iRowH = 0;
        for (i = 0; i < Vdf_lstChecks.Count; i++)
        {
            iCheckW = Math.Max(iCheckW, Vdf_lstChecks[i].PreferredSize.Width);
            iDescW = Math.Max(iDescW, Vdf_lstDescs[i].PreferredSize.Width);
            iRowH = Math.Max(iRowH, Vdf_lstChecks[i].PreferredSize.Height);
            if (Vdf_lstNows[i] != null)
            {   /* A row with the "now" note. */
                iNowW = Math.Max(iNowW, Vdf_lstNows[i].PreferredSize.Width);
            }
        }
        szButton = MainForm.DLG_ButtonSize(new Button[] { Vdf_btnOk, Vdf_btnCancel });

        /* The content width: the widest part, never under the floor. */
        iContent = iPad * 26;
        iContent = Math.Max(iContent, Vdf_lblTitle.PreferredSize.Width);
        iContent = Math.Max(iContent, Vdf_lblIntro.PreferredSize.Width);
        iContent = Math.Max(iContent, iPad + iCheckW + iPad + iDescW + iPad + iNowW);
        iContent = Math.Max(iContent, szButton.Width * 2 + iPad / 2);

        /* The header band, across the whole dialog, its lines inside it. */
        Vdf_lblTitle.Size = Vdf_lblTitle.PreferredSize;
        Vdf_lblTitle.Location = new Point(iPad, iPad * 3 / 4);
        Vdf_lblIntro.Size = Vdf_lblIntro.PreferredSize;
        Vdf_lblIntro.Location = new Point(iPad, Vdf_lblTitle.Bottom + iPad / 4);
        Vdf_pnlHeader.SetBounds(0, 0, iContent + iPad * 2, Vdf_lblIntro.Bottom + iPad * 3 / 4);
        Vdf_lnTop.SetBounds(0, Vdf_pnlHeader.Bottom, Vdf_pnlHeader.Width, 2);
        y = Vdf_lnTop.Bottom + iPad;

        /* The rows: checkbox, then what it is, then "now", centred on it. */
        for (i = 0; i < Vdf_lstChecks.Count; i++)
        {
            Vdf_lstChecks[i].SetBounds(iPad * 2, y, iCheckW, iRowH);
            Vdf_lstDescs[i].Size = Vdf_lstDescs[i].PreferredSize;
            Vdf_lstDescs[i].Location = new Point(Vdf_lstChecks[i].Right + iPad,
                y + (iRowH - Vdf_lstDescs[i].Height) / 2);
            if (Vdf_lstNows[i] != null)
            {   /* The "now" note, in its own column. */
                Vdf_lstNows[i].Size = new Size(iNowW, Vdf_lstNows[i].PreferredSize.Height);
                Vdf_lstNows[i].Location = new Point(Vdf_lstChecks[i].Right + iPad + iDescW
                    + iPad, y + (iRowH - Vdf_lstNows[i].Height) / 2);
            }
            y += iRowH + iPad / 4;
        }

        /* The links, under the checkbox column. */
        y += iPad / 4;
        Vdf_lnkAll.Size = Vdf_lnkAll.PreferredSize;
        Vdf_lnkAll.Location = new Point(iPad * 2, y);
        Vdf_lnkNone.Size = Vdf_lnkNone.PreferredSize;
        Vdf_lnkNone.Location = new Point(Vdf_lnkAll.Right + iPad, y);
        y = Vdf_lnkAll.Bottom + iPad;

        /* The notes: icon at the margin, text wrapped beside it. */
        iNoteX = iPad + Vdf_picWarn.Width + iPad / 2;
        Vdf_picWarn.Location = new Point(iPad, y);
        Vdf_lblRegNote.Size = Vdf_lblRegNote.GetPreferredSize(
            new Size(iPad + iContent - iNoteX, 0));
        Vdf_lblRegNote.Location = new Point(iNoteX, y);
        y = Math.Max(Vdf_picWarn.Bottom, Vdf_lblRegNote.Bottom) + iPad / 2;
        Vdf_picInfo.Location = new Point(iPad, y);
        Vdf_lblWinNote.Size = Vdf_lblWinNote.GetPreferredSize(
            new Size(iPad + iContent - iNoteX, 0));
        Vdf_lblWinNote.Location = new Point(iNoteX, y);
        y = Math.Max(Vdf_picInfo.Bottom, Vdf_lblWinNote.Bottom) + iPad;

        /* The footer: the line across, OK and Cancel at the right. */
        Vdf_lnBottom.SetBounds(iPad, y, iContent, 2);
        y = Vdf_lnBottom.Bottom + iPad * 3 / 4;
        Vdf_btnCancel.SetBounds(iPad + iContent - szButton.Width, y, szButton.Width,
            szButton.Height);
        Vdf_btnOk.SetBounds(Vdf_btnCancel.Left - iPad / 2 - szButton.Width, y,
            szButton.Width, szButton.Height);

        /* The dialog holds it all. */
        ClientSize = new Size(iContent + iPad * 2, Vdf_btnOk.Bottom + iPad * 3 / 4);
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesColorsForm: the Colors dialog (View > Colors...)                   */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* VoorheesColorPick                                                          */
/*                                                                            */
/* How the Colors dialog asks for a colour: the editor supplies the picker    */
/* (MainForm.VIEW_PickColor: the Windows colour dialog, or the test           */
/* harness's answer), so the dialog itself shows no other window.             */
/*                                                                            */
/* Arguments:                                                                 */
/*     owner       : the window the picker belongs to (the Colors dialog).    */
/*     sRole       : the role picked for (VoorheesColorsForm.VCO_ROLEKEYS).   */
/*     colorNow    : its colour now.                                          */
/*     colorPicked : receives the colour picked.                              */
/*                                                                            */
/* Returns:                                                                   */
/*     bool : true when a colour was picked, false for Cancel.                */
/*----------------------------------------------------------------------------*/
delegate bool VoorheesColorPick(IWin32Window owner, string sRole, Color colorNow,
    out Color colorPicked);

/*----------------------------------------------------------------------------*/
/* VoorheesColorsForm                                                         */
/*                                                                            */
/* The Colors dialog, laid out like the Set as Default dialog: a white        */
/* header band with a bold title and a                                        */
/* line of explanation, an etched line; a bold "Names" heading over the three */
/* name roles and a bold "Values" heading over the five value types, each row */
/* a swatch button showing the role's colour (a click opens the colour        */
/* picker), what the role colours, and a sample ("[section]", "42" ...) drawn */
/* in that colour on the tree's background, so a choice shows before OK; the  */
/* note (the colours show while View > Color Keys is on) beside the standard  */
/* information icon; an etched line, then Reset (every role back to its       */
/* default) at the left and OK, Cancel at the right.  It works on its own     */
/* copies of the colours: the editor reads                                    */
/* Vco_arrNameColors / Vco_arrValueColors after OK (Vco_bSaved, a manual      */
/* flag rather than DialogResult, which behaves differently for modal and     */
/* modeless windows).  Every action is a                                      */
/* method the buttons call (Vco_PickRow, Vco_Reset, Vco_Accept, Vco_Cancel),  */
/* so the test harness can work the dialog without showing it.  Laid out      */
/* from measured sizes, again on every resize (Vco_Layout).                   */
/*----------------------------------------------------------------------------*/
class VoorheesColorsForm : Form
{
    /*------------------------------------------------------------------------*/
    /* The rows, in the tree's role order: VoorheesTreeView.VTV_NAME_ (the    */
    /* first VTV_NAMEROLECOUNT rows), then VoorheesFormat.VF_ROLE_.  Each     */
    /* row's key is the role's name for scripts (the harness's "rolecolor"    */
    /* and "colors" commands); its text is what the dialog says.              */
    /*------------------------------------------------------------------------*/

    public static readonly string[] VCO_ROLEKEYS = new string[]
    {
        "top",                                           // VTV_NAME_TOP
        "nested",                                        // VTV_NAME_NESTED
        "entry",                                         // VTV_NAME_ENTRY
        "text",                                          // VF_ROLE_TEXT
        "number",                                        // VF_ROLE_NUMBER
        "boolean",                                       // VF_ROLE_BOOLEAN
        "null",                                          // VF_ROLE_NULL
        "binary"                                         // VF_ROLE_BINARY
    };

    static readonly string[] VCO_ROWTEXTS = new string[]
    {
        "Top level: [section], [registry key], JSON top-level keys",                 // VTV_NAME_TOP
        "Nested: objects and arrays inside others, sections in an auto-saved block", // VTV_NAME_NESTED
        "Entries: the keys and indexes of plain values",                             // VTV_NAME_ENTRY
        "Text: strings, INI and Klipper values, registry strings",                   // VF_ROLE_TEXT
        "Numbers: JSON numbers, registry DWORD and QWORD",                           // VF_ROLE_NUMBER
        "Booleans: true and false",                                                  // VF_ROLE_BOOLEAN
        "No value: null, a registry value to delete",                                // VF_ROLE_NULL
        "Binary: registry hex values"                                                // VF_ROLE_BINARY
    };

    static readonly string[] VCO_SAMPLES = new string[]
    {
        "[section]",                                     // VTV_NAME_TOP: as a section or key path is labelled
        "{object}",                                      // VTV_NAME_NESTED: as a nested JSON object is labelled
        "key",                                           // VTV_NAME_ENTRY
        "text",                                          // VF_ROLE_TEXT
        "42",                                            // VF_ROLE_NUMBER
        "true",                                          // VF_ROLE_BOOLEAN
        "null",                                          // VF_ROLE_NULL
        "01,02,ff"                                       // VF_ROLE_BINARY: as the tree shows registry bytes
    };

    public Color[] Vco_arrNameColors;                    // the name roles' colours as chosen so far (VTV_NAME_ order)
    public Color[] Vco_arrValueColors;                   // the value roles' colours as chosen so far (VF_ROLE_ order)
    public bool Vco_bSaved;                              // OK was pressed
    VoorheesColorPick Vco_pick;                          // asks for a colour
    Panel Vco_pnlHeader;                                 // the white header band
    Label Vco_lblTitle;                                  // its bold title
    Label Vco_lblIntro;                                  // its line of explanation
    Label Vco_lnTop;                                     // the etched line under the header
    Label Vco_lblNames;                                  // "Names" heading, bold
    Label Vco_lblValues;                                 // "Values" heading, bold
    PictureBox Vco_picInfo;                              // the note's information icon
    Label Vco_lblNote;                                   // when the colours show
    Label Vco_lnBottom;                                  // the etched line above the buttons
    List<Button> Vco_lstSwatches;                        // one swatch per row, its colour as its background
    List<Label> Vco_lstLabels;                           // one label per row: what the role colours
    List<Label> Vco_lstSamples;                          // one sample per row, drawn in the role's colour
    Button Vco_btnReset;                                 // every role to its default
    Button Vco_btnOk;                                    // keep the colours
    Button Vco_btnCancel;                                // keep nothing

    /*------------------------------------------------------------------------*/
    /* Vco_Create:                                                            */
    /*                                                                        */
    /* Builds the dialog, not shown, on copies of the colours given.  The     */
    /* row tables must match the role counts (a programming error             */
    /* otherwise, thrown at once).                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     font      : the editor's font (the dialog uses it).                */
    /*     arrNames  : the name roles' colours now (copied).                  */
    /*     arrValues : the value roles' colours now (copied).                 */
    /*     pick      : the colour picker.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesColorsForm : the dialog, laid out.                         */
    /*------------------------------------------------------------------------*/
    public static VoorheesColorsForm Vco_Create(Font font, Color[] arrNames,
        Color[] arrValues, VoorheesColorPick pick)
    {
        VoorheesColorsForm dlg;                          // the new dialog
        Button btnSwatch;                                // one row's swatch
        Label lblRow;                                    // one row's label
        Label lblSample;                                 // one row's sample
        Font fontTitle;                                  // the title's font
        Font fontHeading;                                // the group headings' font
        Bitmap bmpInfo;                                  // the information icon, small
        int i;

        if (VCO_ROLEKEYS.Length != VoorheesTreeView.VTV_NAMEROLECOUNT + VoorheesFormat.VF_ROLECOUNT
            || VCO_ROWTEXTS.Length != VCO_ROLEKEYS.Length
            || VCO_SAMPLES.Length != VCO_ROLEKEYS.Length)
        {   /* The row tables are out of step with the roles: a programming error. */
            throw new InvalidOperationException("Colors dialog rows do not match the colour roles");
        }

        /* The window: a small fixed dialog centred on the editor. */
        dlg = new VoorheesColorsForm();
        dlg.Text = "Colors";
        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
        dlg.MaximizeBox = false;
        dlg.MinimizeBox = false;
        dlg.ShowInTaskbar = false;
        dlg.StartPosition = FormStartPosition.CenterParent;
        dlg.Font = font;
        dlg.Vco_arrNameColors = (Color[])arrNames.Clone();
        dlg.Vco_arrValueColors = (Color[])arrValues.Clone();
        dlg.Vco_bSaved = false;
        dlg.Vco_pick = pick;

        /* The fonts: the title bold and a third larger, the headings bold;   */
        /* both go when the dialog does.                                      */
        fontTitle = new Font(font.FontFamily, font.SizeInPoints * 4f / 3f, FontStyle.Bold);
        fontHeading = new Font(font, FontStyle.Bold);
        dlg.Disposed += delegate
        {
            fontTitle.Dispose();
            fontHeading.Dispose();
        };

        /* The header band: white, the title and a line of explanation   */
        /* (children of the band), an etched line under it.              */
        dlg.Vco_pnlHeader = new Panel();
        dlg.Vco_pnlHeader.BackColor = SystemColors.Window;
        dlg.Controls.Add(dlg.Vco_pnlHeader);
        dlg.Vco_lblTitle = Vco_NewLabel(dlg.Vco_pnlHeader, "Tree colors");
        dlg.Vco_lblTitle.Font = fontTitle;
        dlg.Vco_lblIntro = Vco_NewLabel(dlg.Vco_pnlHeader,
            "Names by kind, values by type. Click a swatch to choose its color.");
        dlg.Vco_lnTop = MainForm.DLG_NewEtchedLine(dlg);

        /* The two group headings. */
        dlg.Vco_lblNames = Vco_NewLabel(dlg, "Names");
        dlg.Vco_lblNames.Font = fontHeading;
        dlg.Vco_lblValues = Vco_NewLabel(dlg, "Values");
        dlg.Vco_lblValues.Font = fontHeading;

        /* One row per role: the swatch (its row number in Tag, for the     */
        /* click), what it colours, and a sample in its colour on the       */
        /* tree's own background, framed like a little piece of the tree.   */
        dlg.Vco_lstSwatches = new List<Button>();
        dlg.Vco_lstLabels = new List<Label>();
        dlg.Vco_lstSamples = new List<Label>();
        for (i = 0; i < VCO_ROLEKEYS.Length; i++)
        {
            btnSwatch = new Button();
            btnSwatch.Tag = i;
            btnSwatch.Text = "";
            btnSwatch.FlatStyle = FlatStyle.Flat;
            btnSwatch.UseVisualStyleBackColor = false;
            btnSwatch.BackColor = dlg.Vco_RowColor(i);
            btnSwatch.AccessibleName = VCO_ROWTEXTS[i];
            btnSwatch.Click += dlg.Vco_SwatchClick;
            dlg.Controls.Add(btnSwatch);
            dlg.Vco_lstSwatches.Add(btnSwatch);

            lblRow = Vco_NewLabel(dlg, VCO_ROWTEXTS[i]);
            dlg.Vco_lstLabels.Add(lblRow);

            lblSample = Vco_NewLabel(dlg, VCO_SAMPLES[i]);
            lblSample.BackColor = SystemColors.Window;
            lblSample.ForeColor = dlg.Vco_RowColor(i);
            lblSample.BorderStyle = BorderStyle.FixedSingle;
            lblSample.TextAlign = ContentAlignment.MiddleLeft;
            dlg.Vco_lstSamples.Add(lblSample);
        }

        /* The note beside the standard information icon (the picture goes   */
        /* when the dialog does).                                            */
        bmpInfo = MainForm.DLG_SmallIconBitmap(SystemIcons.Information);
        dlg.Disposed += delegate { bmpInfo.Dispose(); };
        dlg.Vco_picInfo = new PictureBox();
        dlg.Vco_picInfo.Image = bmpInfo;
        dlg.Vco_picInfo.SizeMode = PictureBoxSizeMode.Zoom;
        dlg.Vco_picInfo.Size = SystemInformation.SmallIconSize;
        dlg.Controls.Add(dlg.Vco_picInfo);
        dlg.Vco_lblNote = Vco_NewLabel(dlg, "These colors show while View > Color Keys "
            + "is on. Repeated keys stay red and comments stay grey.");
        dlg.Vco_lnBottom = MainForm.DLG_NewEtchedLine(dlg);

        /* Reset at the left, OK and Cancel at the right. */
        dlg.Vco_btnReset = Vco_NewButton(dlg, "Reset");
        dlg.Vco_btnReset.Click += delegate { dlg.Vco_Reset(); };
        dlg.Vco_btnOk = Vco_NewButton(dlg, "OK");
        dlg.Vco_btnOk.Click += delegate { dlg.Vco_Accept(); };
        dlg.Vco_btnCancel = Vco_NewButton(dlg, "Cancel");
        dlg.Vco_btnCancel.Click += delegate { dlg.Vco_Cancel(); };
        dlg.AcceptButton = dlg.Vco_btnOk;
        dlg.CancelButton = dlg.Vco_btnCancel;

        /* Placed from measured sizes, now and on every resize. */
        dlg.Resize += delegate { dlg.Vco_Layout(); };
        dlg.Vco_Layout();

        /* Ready to show (or to be worked by the harness). */
        return(dlg);
    }

    /*------------------------------------------------------------------------*/
    /* Vco_NewLabel:                                                          */
    /*                                                                        */
    /* A label of the dialog's: its text, sized by Vco_Layout from its        */
    /* measured size (not AutoSize, whose size is not final until the         */
    /* dialog is laid out by Windows).                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     parent : the control it goes in (the dialog or the header band).   */
    /*     sText  : the text.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Label : the label, added to its parent.                            */
    /*------------------------------------------------------------------------*/
    static Label Vco_NewLabel(Control parent, string sText)
    {
        Label lbl;                                       // the new label

        lbl = new Label();
        lbl.AutoSize = false;
        lbl.Text = sText;
        parent.Controls.Add(lbl);

        /* Placed later. */
        return(lbl);
    }

    /*-------------------------------------------------------------------------*/
    /* Vco_NewButton:                                                          */
    /*                                                                         */
    /* A text button of the dialog's (Reset, OK, Cancel), sized by Vco_Layout. */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     dlg   : the dialog.                                                 */
    /*     sText : the caption.                                                */
    /*                                                                         */
    /* Returns:                                                                */
    /*     Button : the button, added to the dialog.                           */
    /*-------------------------------------------------------------------------*/
    static Button Vco_NewButton(VoorheesColorsForm dlg, string sText)
    {
        Button btn;                                      // the new button

        btn = new Button();
        btn.AutoSize = false;
        btn.Text = sText;
        dlg.Controls.Add(btn);

        /* Placed later. */
        return(btn);
    }

    /*------------------------------------------------------------------------*/
    /* Vco_RowOf:                                                             */
    /*                                                                        */
    /* The row of a role named by its key (VCO_ROLEKEYS).                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRole : the key, e.g. "number".                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the row, or -1 for an unknown name.                          */
    /*------------------------------------------------------------------------*/
    public static int Vco_RowOf(string sRole)
    {
        /* Its place in the table (-1 when absent). */
        return(Array.IndexOf(VCO_ROLEKEYS, sRole));
    }

    /*------------------------------------------------------------------------*/
    /* Vco_RowColor:                                                          */
    /*                                                                        */
    /* A row's colour as chosen so far: a name role's from Vco_arrNameColors, */
    /* a value role's from Vco_arrValueColors.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow : the row.                                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : its colour.                                                */
    /*------------------------------------------------------------------------*/
    public Color Vco_RowColor(int iRow)
    {
        if (iRow < VoorheesTreeView.VTV_NAMEROLECOUNT)
        {   /* A name role. */
            return(Vco_arrNameColors[iRow]);
        }

        /* A value role, after the name roles. */
        return(Vco_arrValueColors[iRow - VoorheesTreeView.VTV_NAMEROLECOUNT]);
    }

    /*------------------------------------------------------------------------*/
    /* Vco_SetRowColor:                                                       */
    /*                                                                        */
    /* Changes a row's colour: in its table, and on its swatch.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow  : the row.                                                   */
    /*     color : the new colour.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the colour is chosen and shown.                             */
    /*------------------------------------------------------------------------*/
    void Vco_SetRowColor(int iRow, Color color)
    {
        if (iRow < VoorheesTreeView.VTV_NAMEROLECOUNT)
        {   /* A name role. */
            Vco_arrNameColors[iRow] = color;
        }
        else
        {   /* A value role, after the name roles. */
            Vco_arrValueColors[iRow - VoorheesTreeView.VTV_NAMEROLECOUNT] = color;
        }

        /* The swatch and the sample show it. */
        Vco_lstSwatches[iRow].BackColor = color;
        Vco_lstSamples[iRow].ForeColor = color;
    }

    /*------------------------------------------------------------------------*/
    /* Vco_SwatchColor:                                                       */
    /*                                                                        */
    /* The colour a row's swatch shows (for the harness: what the user sees). */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow : the row.                                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : the swatch's background.                                   */
    /*------------------------------------------------------------------------*/
    public Color Vco_SwatchColor(int iRow)
    {
        /* Its background. */
        return(Vco_lstSwatches[iRow].BackColor);
    }

    /*------------------------------------------------------------------------*/
    /* Vco_SampleColor:                                                       */
    /*                                                                        */
    /* The colour a row's sample is drawn in (for the harness: it must follow */
    /* the swatch).                                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow : the row.                                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : the sample's text colour.                                  */
    /*------------------------------------------------------------------------*/
    public Color Vco_SampleColor(int iRow)
    {
        /* Its text colour. */
        return(Vco_lstSamples[iRow].ForeColor);
    }

    /*------------------------------------------------------------------------*/
    /* Vco_SwatchClick:                                                       */
    /*                                                                        */
    /* A swatch was clicked: pick that row's colour.  The row comes from the  */
    /* button's Tag (one handler for every swatch).                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sender : the swatch.                                               */
    /*     e      : unused.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : see Vco_PickRow.                                            */
    /*------------------------------------------------------------------------*/
    void Vco_SwatchClick(object sender, EventArgs e)
    {
        /* The clicked swatch's row, from its Tag. */
        Vco_PickRow((int)((Button)sender).Tag);
    }

    /*------------------------------------------------------------------------*/
    /* Vco_PickRow:                                                           */
    /*                                                                        */
    /* Asks the picker for a row's colour, starting at its colour now; a      */
    /* colour picked becomes the row's, Cancel leaves it.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iRow : the row.                                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the row's colour may change.                                */
    /*------------------------------------------------------------------------*/
    public void Vco_PickRow(int iRow)
    {
        Color colorPicked;                               // the colour the picker gave

        if (Vco_pick(this, VCO_ROLEKEYS[iRow], Vco_RowColor(iRow), out colorPicked))
        {   /* Picked: the row takes it. */
            Vco_SetRowColor(iRow, colorPicked);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vco_Reset:                                                             */
    /*                                                                        */
    /* Reset: every row back to its role's default (dark blue names, black    */
    /* values: VoorheesTreeView.Vtv_DefaultNameColors / Vtv_DefaultValue-     */
    /* Colors).  Nothing is kept until OK.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : every row shows its default.                                */
    /*------------------------------------------------------------------------*/
    public void Vco_Reset()
    {
        Color[] arrNames;                                // the name roles' defaults
        Color[] arrValues;                               // the value roles' defaults
        int i;

        arrNames = VoorheesTreeView.Vtv_DefaultNameColors();
        arrValues = VoorheesTreeView.Vtv_DefaultValueColors();
        for (i = 0; i < VCO_ROLEKEYS.Length; i++)
        {
            if (i < VoorheesTreeView.VTV_NAMEROLECOUNT)
            {   /* A name role. */
                Vco_SetRowColor(i, arrNames[i]);
            }
            else
            {   /* A value role. */
                Vco_SetRowColor(i, arrValues[i - VoorheesTreeView.VTV_NAMEROLECOUNT]);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vco_Accept:                                                            */
    /*                                                                        */
    /* OK: the colours chosen are to be kept (Vco_bSaved) and the dialog      */
    /* closes.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dialog is closing.                                      */
    /*------------------------------------------------------------------------*/
    public void Vco_Accept()
    {
        /* Kept: the editor reads the flag and the tables once the dialog   */
        /* has closed (a dialog never shown -- the harness's -- is simply   */
        /* disposed by Close, its fields still readable).                   */
        Vco_bSaved = true;
        Close();
    }

    /*------------------------------------------------------------------------*/
    /* Vco_Cancel:                                                            */
    /*                                                                        */
    /* Cancel: nothing is kept (Vco_bSaved stays false) and the dialog        */
    /* closes.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the dialog is closing.                                      */
    /*------------------------------------------------------------------------*/
    public void Vco_Cancel()
    {
        /* Closed with the flag still false: the editor keeps nothing. */
        Close();
    }

    /*------------------------------------------------------------------------*/
    /* Vco_Layout:                                                            */
    /*                                                                        */
    /* Places everything from measured sizes, the font's height (iPad) giving */
    /* margins and spacing, as the Set as Default dialog does (Vdf_Layout).   */
    /* First the widths: the description column as wide as its widest label,  */
    /* the sample column as its widest sample plus padding, and the content   */
    /* as the widest of the header's lines, a row (swatch, description,       */
    /* sample), the three buttons, and a floor of 26 font heights.  Then, top */
    /* down: the header band across the whole width (title, line), the        */
    /* etched line; the bold "Names" heading at the margin and the name rows  */
    /* indented under it, then "Values" and the value rows -- each row a      */
    /* swatch (three font heights wide, a button's height tall), its          */
    /* description centred beside it, its sample in a column of its own; the  */
    /* information icon and the note wrapped beside it; the etched line;      */
    /* Reset at the left and OK, Cancel at the right; the dialog sized to     */
    /* hold it.  Text buttons are at least five font heights wide (the usual  */
    /* Windows button width at the usual font).                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are placed and the client size set.            */
    /*------------------------------------------------------------------------*/
    public void Vco_Layout()
    {
        int iPad;                                        // margin and spacing, from the font
        int iSwatchW;                                    // a swatch's width
        int iRowH;                                       // a row's (a swatch's, every button's) height
        int iButtonW;                                    // the text buttons' width
        int iDescW;                                      // the description column's width
        int iSampleW;                                    // the sample column's width
        int iContent;                                    // the content's width
        int iNoteX;                                      // where the note's text starts
        int y;                                           // the next row's top
        Label lblHeading;                                // a group heading, or null
        Button btnSwatch;                                // one row's swatch
        Label lblRow;                                    // one row's description
        Label lblSample;                                 // one row's sample
        int i;

        iPad = Font.Height;
        iSwatchW = iPad * 3;
        iRowH = Vco_btnOk.PreferredSize.Height;
        iButtonW = Math.Max(iPad * 5, Math.Max(Vco_btnReset.PreferredSize.Width,
            Math.Max(Vco_btnOk.PreferredSize.Width, Vco_btnCancel.PreferredSize.Width)));

        /* The columns, each as wide as its widest entry. */
        iDescW = 0;
        iSampleW = 0;
        for (i = 0; i < Vco_lstSwatches.Count; i++)
        {
            iDescW = Math.Max(iDescW, Vco_lstLabels[i].PreferredSize.Width);
            iSampleW = Math.Max(iSampleW, Vco_lstSamples[i].PreferredSize.Width);
        }
        iSampleW += iPad;

        /* The content width: the widest part, never under the floor. */
        iContent = iPad * 26;
        iContent = Math.Max(iContent, Vco_lblTitle.PreferredSize.Width);
        iContent = Math.Max(iContent, Vco_lblIntro.PreferredSize.Width);
        iContent = Math.Max(iContent, iPad + iSwatchW + iPad / 2 + iDescW + iPad + iSampleW);
        iContent = Math.Max(iContent, iButtonW * 3 + iPad * 2);

        /* The header band, across the whole dialog, its lines inside it. */
        Vco_lblTitle.Size = Vco_lblTitle.PreferredSize;
        Vco_lblTitle.Location = new Point(iPad, iPad * 3 / 4);
        Vco_lblIntro.Size = Vco_lblIntro.PreferredSize;
        Vco_lblIntro.Location = new Point(iPad, Vco_lblTitle.Bottom + iPad / 4);
        Vco_pnlHeader.SetBounds(0, 0, iContent + iPad * 2, Vco_lblIntro.Bottom + iPad * 3 / 4);
        Vco_lnTop.SetBounds(0, Vco_pnlHeader.Bottom, Vco_pnlHeader.Width, 2);
        y = Vco_lnTop.Bottom + iPad * 3 / 4;

        for (i = 0; i < Vco_lstSwatches.Count; i++)
        {
            /* A group's first row has the group's heading above it. */
            if (i == 0)
            {   /* The name group starts. */
                lblHeading = Vco_lblNames;
            }
            else if (i == VoorheesTreeView.VTV_NAMEROLECOUNT)
            {   /* The value group starts, a little apart from the names. */
                y += iPad / 2;
                lblHeading = Vco_lblValues;
            }
            else
            {   /* Inside a group: no heading. */
                lblHeading = null;
            }

            if (lblHeading != null)
            {   /* The heading, at the margin. */
                lblHeading.Size = lblHeading.PreferredSize;
                lblHeading.Location = new Point(iPad, y);
                y = lblHeading.Bottom + iPad / 4;
            }

            /* The swatch, indented; its description centred beside it; the   */
            /* sample in its own column, as tall as the swatch.               */
            btnSwatch = Vco_lstSwatches[i];
            btnSwatch.SetBounds(iPad * 2, y, iSwatchW, iRowH);
            lblRow = Vco_lstLabels[i];
            lblRow.Size = lblRow.PreferredSize;
            lblRow.Location = new Point(btnSwatch.Right + iPad / 2,
                y + (iRowH - lblRow.Height) / 2);
            lblSample = Vco_lstSamples[i];
            lblSample.SetBounds(btnSwatch.Right + iPad / 2 + iDescW + iPad, y, iSampleW, iRowH);
            y = btnSwatch.Bottom + iPad / 4;
        }

        /* The note: the icon at the margin, the text wrapped beside it. */
        y += iPad / 2;
        iNoteX = iPad + Vco_picInfo.Width + iPad / 2;
        Vco_picInfo.Location = new Point(iPad, y);
        Vco_lblNote.Size = Vco_lblNote.GetPreferredSize(new Size(iPad + iContent - iNoteX, 0));
        Vco_lblNote.Location = new Point(iNoteX, y);
        y = Math.Max(Vco_picInfo.Bottom, Vco_lblNote.Bottom) + iPad;

        /* The footer: the line across; Reset at the left; OK and Cancel at   */
        /* the right, Cancel last.                                            */
        Vco_lnBottom.SetBounds(iPad, y, iContent, 2);
        y = Vco_lnBottom.Bottom + iPad * 3 / 4;
        Vco_btnReset.SetBounds(iPad, y, iButtonW, iRowH);
        Vco_btnCancel.SetBounds(iPad + iContent - iButtonW, y, iButtonW, iRowH);
        Vco_btnOk.SetBounds(Vco_btnCancel.Left - iPad / 2 - iButtonW, y, iButtonW, iRowH);

        /* The dialog holds it all. */
        ClientSize = new Size(iContent + iPad * 2, Vco_btnOk.Bottom + iPad * 3 / 4);
    }
}

/*----------------------------------------------------------------------------*/
/* AboutForm: the About box                                                   */
/*----------------------------------------------------------------------------*/

/*----------------------------------------------------------------------------*/
/* AboutForm                                                                  */
/*                                                                            */
/* Help > About, framed like the other dialogs:                               */
/*     o a white body band: the hockey mask icon large at the top left (as    */
/*       before), and to its right the name, the version (grey), the tagline  */
/*       (italic red), a short red accent rule, the description (word for     */
/*       word, its lines as written), a bold "Features" heading and the       */
/*       features as a bulleted list (the old "Features:" sentence's own      */
/*       words, set out rather than run together);                            */
/*     o an etched line, then OK at the bottom right on the dialog's own      */
/*       grey, as the Set as Default and Colors dialogs end.                  */
/*----------------------------------------------------------------------------*/
class AboutForm : Form
{
    /*----------------------------------------------------------------------*/
    /* Spacing, pixels, the icon's design size, and the colours and text.   */
    /* Everything else is measured from the fonts and text.  The bullet is  */
    /* built from its code point, which keeps this file pure ASCII.         */
    /*----------------------------------------------------------------------*/

    const int ABF_PAD = 20;                                                // space round the content and between the columns
    const int ABF_LINEGAP = 4;                                             // space between the lines of text
    const int ABF_ICONSIZE = 128;                                          // icon size at 96 dpi, pixels; scaled to the screen
    const float ABF_DESIGNDPI = 96f;                                       // the dpi ABF_ICONSIZE is designed for
    const int ABF_ACCENTWIDTH = 48;                                        // the accent rule's length, pixels at 96 dpi (a short rule, not a full-width line)
    static readonly Color ABF_COLORMUTED = Color.FromArgb(100, 100, 100);  // the version line
    static readonly Color ABF_COLORACCENT = Color.FromArgb(192, 32, 32);   // the tagline and the accent rule
    static readonly string ABF_BULLET = ((char)0x2022).ToString() + "  ";  // U+2022 bullet and a gap, before each feature

    static readonly string[] ABF_FEATURES = new string[]
    {
        "Tree-based editing",                            // the tree and the edit panel
        "Comments",                                      // kept, edited, added
        "Raw text",                                      // the Raw box and the raw view
        "Undo",                                          // and redo
        "Find and replace",                              // Search menu
        "Explorer integration"                           // Tools menu
    };

    Panel Abf_pnlBody;                                   // the white body band (icon and text)
    PictureBox Abf_picIcon;                              // the hockey mask, large
    Label Abf_lblName;                                   // "Voorhees"
    Label Abf_lblVersion;                                // "Version x.y.z"
    Label Abf_lblTagline;                                // the tagline
    Label Abf_lnAccent;                                  // the short red rule under the tagline
    Label Abf_lblDesc;                                   // what the program is (two lines)
    Label Abf_lblFeaturesHead;                           // "Features", bold
    Label Abf_lblFeatures;                               // the features, bulleted
    Label Abf_lnFooter;                                  // the etched line over the footer
    Button Abf_btnOk;                                    // closes the box
    int Abf_iIconSize;                                   // icon size on this screen, pixels
    int Abf_iAccentWidth;                                // the accent rule's length on this screen, pixels
    List<Font> Abf_lstFonts;                             // the fonts made here, freed with the box

    /*------------------------------------------------------------------------*/
    /* AboutForm:                                                             */
    /*                                                                        */
    /* Builds the About box.  The icon is the largest image from the          */
    /* copy of Voorhees.ico compiled into the exe, shown at                   */
    /* ABF_ICONSIZE scaled to the screen's dpi; if it cannot be read the      */
    /* box simply has no picture.  Enter and Escape both close it.  No        */
    /* control is given a position here: the box is sized to its              */
    /* measured content and ABF_Layout places everything, here and on         */
    /* every resize.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     AboutForm : the box, not yet shown.                                */
    /*------------------------------------------------------------------------*/
    public AboutForm()
    {
        Stream icoStream;                                // embedded icon resource
        Icon icon;                                       // the icon at the size shown
        Graphics g;                                      // reads the screen's dpi

        Text = "About Voorhees";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        /* The fonts made here go when the box does. */
        Abf_lstFonts = new List<Font>();
        Disposed += delegate
        {
            foreach (Font font in Abf_lstFonts)
            {
                font.Dispose();
            }
        };

        /* Icon and accent sizes for this screen: the design sizes at 96   */
        /* dpi, scaled, so they keep their size relative to the text.      */
        g = CreateGraphics();
        using (g)
        {
            Abf_iIconSize = (int)Math.Round(ABF_ICONSIZE * g.DpiX / ABF_DESIGNDPI);
            Abf_iAccentWidth = (int)Math.Round(ABF_ACCENTWIDTH * g.DpiX / ABF_DESIGNDPI);
        }

        /* The white body band; the icon and the text go in it. */
        Abf_pnlBody = new Panel();
        Abf_pnlBody.BackColor = SystemColors.Window;
        Controls.Add(Abf_pnlBody);

        /* Left: the icon, large. */
        Abf_picIcon = new PictureBox();
        Abf_picIcon.Size = new Size(Abf_iIconSize, Abf_iIconSize);
        Abf_picIcon.SizeMode = PictureBoxSizeMode.Zoom;
        Abf_picIcon.BackColor = Color.Transparent;

        try
        {
            icoStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Voorhees.ico");
            if (icoStream != null)
            {   /* Resource present: take the image nearest the size shown, then release the stream. */
                icon = new Icon(icoStream, Abf_iIconSize, Abf_iIconSize);
                Abf_picIcon.Image = icon.ToBitmap();
                icoStream.Close();
            }
        }
        catch
        {   /* Unreadable icon resource: show no picture. */
        }

        Abf_pnlBody.Controls.Add(Abf_picIcon);

        /* Right: the name, large and bold. */
        Abf_lblName = ABF_NewLabel("Voorhees", ABF_Font(20f, FontStyle.Bold));

        /* The version, from the build-generated Version.cs, in grey. */
        Abf_lblVersion = ABF_NewLabel("Version " + VoorheesVersion.V,
            ABF_Font(10f, FontStyle.Regular));
        Abf_lblVersion.ForeColor = ABF_COLORMUTED;

        /* The tagline, in italic red, and a short red rule under it. */
        Abf_lblTagline = ABF_NewLabel("\"This JSON app is a real killer.\"",
            ABF_Font(10f, FontStyle.Italic));
        Abf_lblTagline.ForeColor = ABF_COLORACCENT;
        Abf_lnAccent = ABF_NewLabel("", ABF_Font(9f, FontStyle.Regular));
        Abf_lnAccent.BackColor = ABF_COLORACCENT;

        /* What it is, in its own words (its line breaks are part of the text). */
        Abf_lblDesc = ABF_NewLabel("A hierarchical JSON, INI, Klipper config and\r\n"
            + "registry file editor for Windows.\r\n"
            + "Built with WinForms and its own lossless readers.",
            ABF_Font(9f, FontStyle.Regular));

        /* Its features: a bold heading over a bulleted list. */
        Abf_lblFeaturesHead = ABF_NewLabel("Features", ABF_Font(9f, FontStyle.Bold));
        Abf_lblFeatures = ABF_NewLabel(ABF_BULLET + string.Join("\r\n" + ABF_BULLET,
            ABF_FEATURES), ABF_Font(9f, FontStyle.Regular));

        /* Under the band: an etched line, then OK at the bottom right. */
        Abf_lnFooter = MainForm.DLG_NewEtchedLine(this);
        Abf_btnOk = new Button();
        Abf_btnOk.Text = "OK";
        Abf_btnOk.Click += delegate { Close(); };
        Controls.Add(Abf_btnOk);

        AcceptButton = Abf_btnOk;
        CancelButton = Abf_btnOk;

        /* Place everything (sizing the box to its content), and place it   */
        /* again whenever the size changes.                                 */
        ABF_Layout();
        Resize += delegate { ABF_Layout(); };
    }

    /*----------------------------------------------------------------------*/
    /* ABF_Font:                                                            */
    /*                                                                      */
    /* A Segoe UI font for the box, remembered so it is freed with it.      */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     fSize : the size, points.                                        */
    /*     style : the style.                                               */
    /*                                                                      */
    /* Returns:                                                             */
    /*     Font : the font.                                                 */
    /*----------------------------------------------------------------------*/
    Font ABF_Font(float fSize, FontStyle style)
    {
        Font font;                                       // the new font

        font = new Font("Segoe UI", fSize, style);
        Abf_lstFonts.Add(font);

        /* Made and remembered. */
        return(font);
    }

    /*----------------------------------------------------------------------*/
    /* ABF_NewLabel:                                                        */
    /*                                                                      */
    /* A label in the body band, sized by ABF_Layout from its measured      */
    /* size (not AutoSize, whose size is not final until Windows lays the   */
    /* box out).                                                            */
    /*                                                                      */
    /* Arguments:                                                           */
    /*     sText : its text.                                                */
    /*     font  : its font.                                                */
    /*                                                                      */
    /* Returns:                                                             */
    /*     Label : the label, added to the band.                            */
    /*----------------------------------------------------------------------*/
    Label ABF_NewLabel(string sText, Font font)
    {
        Label lbl;                                       // the new label

        lbl = new Label();
        lbl.AutoSize = false;
        lbl.Text = sText;
        lbl.Font = font;
        Abf_pnlBody.Controls.Add(lbl);

        /* Placed later. */
        return(lbl);
    }

    /*------------------------------------------------------------------------*/
    /* ABF_Layout:                                                            */
    /*                                                                        */
    /* Places everything from measured sizes and sizes the box to hold it:    */
    /* in the body band, the icon at the top left (ABF_PAD in, as it always   */
    /* was) and the text column to its right, top down -- name, version,      */
    /* tagline, the accent rule (ABF_LINEGAP above and three below), the      */
    /* description, the "Features" heading and the bulleted list; the band    */
    /* as tall as the taller of icon and text plus ABF_PAD, across the        */
    /* whole box; the etched line under it; OK at the bottom right with       */
    /* three-quarters of ABF_PAD round it.  The box is as wide as icon and    */
    /* text side by side with ABF_PAD round and between them.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the controls are placed and the client size set.            */
    /*------------------------------------------------------------------------*/
    void ABF_Layout()
    {
        Label[] arrText;                                 // the text column's labels, top down
        Size szButton;                                   // the OK button
        int iTextLeft;                                   // x of the text column
        int iTextW;                                      // its width
        int iBodyH;                                      // the body band's height
        int iWidth;                                      // the box's client width
        int y;                                           // top of the line being placed
        int i;

        if (Abf_btnOk == null)
        {   /* A resize raised while the controls are still being built. */
            return;
        }

        /* Every text label at its measured size; the column as wide as the widest. */
        arrText = new Label[] { Abf_lblName, Abf_lblVersion, Abf_lblTagline, Abf_lblDesc,
            Abf_lblFeaturesHead, Abf_lblFeatures };
        iTextW = Abf_iAccentWidth;
        for (i = 0; i < arrText.Length; i++)
        {
            arrText[i].Size = arrText[i].PreferredSize;
            iTextW = Math.Max(iTextW, arrText[i].Width);
        }

        /* Icon, top left of the band. */
        Abf_picIcon.Location = new Point(ABF_PAD, ABF_PAD);

        /* Text column, right of the icon, top down. */
        iTextLeft = Abf_picIcon.Right + ABF_PAD;
        y = ABF_PAD;
        Abf_lblName.Location = new Point(iTextLeft, y);
        y = Abf_lblName.Bottom + ABF_LINEGAP;
        Abf_lblVersion.Location = new Point(iTextLeft, y);
        y = Abf_lblVersion.Bottom + ABF_LINEGAP;
        Abf_lblTagline.Location = new Point(iTextLeft, y);
        y = Abf_lblTagline.Bottom + ABF_LINEGAP * 2;
        Abf_lnAccent.SetBounds(iTextLeft, y, Abf_iAccentWidth, 2);
        y = Abf_lnAccent.Bottom + ABF_LINEGAP * 3;
        Abf_lblDesc.Location = new Point(iTextLeft, y);
        y = Abf_lblDesc.Bottom + ABF_LINEGAP * 3;
        Abf_lblFeaturesHead.Location = new Point(iTextLeft, y);
        y = Abf_lblFeaturesHead.Bottom + ABF_LINEGAP;
        Abf_lblFeatures.Location = new Point(iTextLeft, y);

        /* The band: icon and text with ABF_PAD round them, across the box. */
        iWidth = iTextLeft + iTextW + ABF_PAD;
        iBodyH = Math.Max(Abf_picIcon.Bottom, Abf_lblFeatures.Bottom) + ABF_PAD;
        Abf_pnlBody.SetBounds(0, 0, iWidth, iBodyH);

        /* The footer: the etched line, then OK at the bottom right. */
        Abf_lnFooter.SetBounds(0, iBodyH, iWidth, 2);
        szButton = MainForm.DLG_ButtonSize(new Button[] { Abf_btnOk });
        Abf_btnOk.SetBounds(iWidth - ABF_PAD * 3 / 4 - szButton.Width,
            Abf_lnFooter.Bottom + ABF_PAD * 3 / 4, szButton.Width, szButton.Height);

        /* The box holds it all. */
        ClientSize = new Size(iWidth, Abf_btnOk.Bottom + ABF_PAD * 3 / 4);
    }

    /*------------------------------------------------------------------------*/
    /* OnKeyDown:                                                             */
    /*                                                                        */
    /* Escape closes the box (KeyPreview routes keys here first).             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     e : the key pressed.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the box may close.                                          */
    /*------------------------------------------------------------------------*/
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {   /* Escape: close. */
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
