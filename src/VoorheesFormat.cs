/*----------------------------------------------------------------------------*/
/* VoorheesFormat.cs                                                          */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* The file formats Voorhees edits, and the ONE place that knows which        */
/* module handles which.  The model (VoorheesDocument.cs), the tree and the   */
/* editor know no format: every question whose answer depends on the format   */
/* -- reading, writing, rendering an entry's raw text, what a comment looks   */
/* like -- is asked here, and each function below is a switch on the          */
/* document's format id that calls that format's module by its own name.      */
/*                                                                            */
/* No interface or virtual dispatch is used: the project's rules forbid       */
/* shared names (no overloading, no polymorphic calls in new code), so each   */
/* module's functions keep their unique prefixes (Vjs_ for JSON, and so on)   */
/* and this switch is the dispatch table.                                     */
/*                                                                            */
/* Formats: JSON (VoorheesJson.cs), INI                                       */
/* (VoorheesIni.cs), the three Klipper-family dialects (VoorheesCfg.cs) and   */
/* registry files (VoorheesReg.cs).  The "no module" fallbacks below remain   */
/* for an id that names no format.                                            */
/*                                                                            */
/* Class:                                                                     */
/*     VoorheesFormat (Vf_) : static functions only; nothing is kept between  */
/*                            calls, so any of them may run on a worker       */
/*                            thread.                                         */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*----------------------------------------------------------------------------*/
/* VoorheesFormat                                                             */
/*                                                                            */
/* Format ids, and the dispatch of every format-dependent question.           */
/*----------------------------------------------------------------------------*/
static class VoorheesFormat
{
    /*------------------------------------------------------------------------*/
    /* Format ids: one per format AND dialect, since each dialect reads and   */
    /* writes differently.  Stored in VoorheesDocument.Vd_iFormat.            */
    /*------------------------------------------------------------------------*/

    public const int VF_JSON = 1;                        // JSON (comments allowed: JSONC)
    public const int VF_INI = 2;                         // classic Windows INI
    public const int VF_KLIPPER = 3;                     // Klipper printer.cfg dialect
    public const int VF_MOONRAKER = 4;                   // Moonraker moonraker.conf dialect
    public const int VF_KLIPPERSCREEN = 5;               // KlipperScreen KlipperScreen.conf dialect
    public const int VF_REG = 6;                         // Windows registry file (.reg)
    public const int VF_DETECT = 0;                      // not a format: "work it out from the file" (open, command line)

    /*------------------------------------------------------------------------*/
    /* The Open dialog's entries, in order (FilterIndex counts from 1): all   */
    /* supported files (detected), one entry per format (forcing it), and     */
    /* all files (detected).                                                  */
    /*------------------------------------------------------------------------*/

    static readonly int[] VF_OPENFILTERFORMATS = new int[]
    {
        VF_DETECT,                                       // All supported files: detected
        VF_JSON,                                         // JSON files
        VF_INI,                                          // INI files
        VF_KLIPPER,                                      // Klipper config
        VF_MOONRAKER,                                    // Moonraker config
        VF_KLIPPERSCREEN,                                // KlipperScreen config
        VF_REG,                                          // Registry files
        VF_DETECT                                        // All files: detected
    };

    /*------------------------------------------------------------------------*/
    /* Vf_Patterns:                                                           */
    /*                                                                        */
    /* A format's file name patterns, for the file dialogs.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : a VF_ id.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "*.ini;*.inf"; "*.*" for VF_DETECT.                  */
    /*------------------------------------------------------------------------*/
    static string Vf_Patterns(int iFormat)
    {
        switch (iFormat)
        {
            case VF_JSON:          return("*.json;*.jsonc");                    // JSON and JSONC
            case VF_INI:           return("*.ini;*.inf");                       // INI 4
            case VF_KLIPPER:       return("*.cfg");                             // printer.cfg and its includes
            case VF_MOONRAKER:     return("*.conf");                            // moonraker.conf
            case VF_KLIPPERSCREEN: return("*.conf");                            // KlipperScreen.conf
            case VF_REG:           return("*.reg");                             // registry files
            default:               return("*.*");                               // anything
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_DialogFilter:                                                       */
    /*                                                                        */
    /* The Save As dialog's filter for a document: its own format's files,    */
    /* then all files.                                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the filter, in OpenFileDialog / SaveFileDialog form.      */
    /*------------------------------------------------------------------------*/
    public static string Vf_DialogFilter(int iFormat)
    {
        /* "Name files (patterns)|patterns|All files (*.*)|*.*". */
        return(Vf_FilterEntry(iFormat) + "|All files (*.*)|*.*");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_FilterEntry:                                                        */
    /*                                                                        */
    /* One format's entry in a dialog filter: "Name (patterns)|patterns".     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : a VF_ id.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the entry.                                                */
    /*------------------------------------------------------------------------*/
    static string Vf_FilterEntry(int iFormat)
    {
        string sName;                                    // what the entry is called

        sName = Vf_Name(iFormat);
        if (iFormat == VF_JSON || iFormat == VF_INI)
        {   /* "JSON files", "INI files". */
            sName += " files";
        }
        else if (iFormat == VF_REG)
        {   /* "Registry file" -> "Registry files". */
            sName += "s";
        }

        /* Name with its patterns, then the patterns. */
        return(sName + " (" + Vf_Patterns(iFormat) + ")|" + Vf_Patterns(iFormat));
    }

    /*------------------------------------------------------------------------*/
    /* Vf_OpenDialogFilter:                                                   */
    /*                                                                        */
    /* The Open dialog's filter: all supported files, one entry per format    */
    /* (choosing one forces that format, INI 4 / K1), and all files.  Its     */
    /* entries are in VF_OPENFILTERFORMATS' order (Vf_OpenDialogFormat).      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the filter.                                               */
    /*------------------------------------------------------------------------*/
    public static string Vf_OpenDialogFilter()
    {
        StringBuilder sb;                                // the filter being built
        int i;

        sb = new StringBuilder();
        sb.Append("All supported files (*.json;*.jsonc;*.ini;*.inf;*.cfg;*.conf;*.reg)"
            + "|*.json;*.jsonc;*.ini;*.inf;*.cfg;*.conf;*.reg");
        for (i = 1; i < VF_OPENFILTERFORMATS.Length - 1; i++)
        {
            sb.Append('|');
            sb.Append(Vf_FilterEntry(VF_OPENFILTERFORMATS[i]));
        }
        sb.Append("|All files (*.*)|*.*");

        /* Every entry, in order. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vf_OpenDialogFormat:                                                   */
    /*                                                                        */
    /* The format an Open dialog entry stands for.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFilterIndex : the dialog's FilterIndex (from 1).                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : a VF_ id to force, or VF_DETECT.                             */
    /*------------------------------------------------------------------------*/
    public static int Vf_OpenDialogFormat(int iFilterIndex)
    {
        if (iFilterIndex < 1 || iFilterIndex > VF_OPENFILTERFORMATS.Length)
        {   /* Out of range: work it out from the file. */
            return(VF_DETECT);
        }

        /* That entry's format. */
        return(VF_OPENFILTERFORMATS[iFilterIndex - 1]);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_DefaultExtension:                                                   */
    /*                                                                        */
    /* The extension Save As adds to a name typed without one.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the extension without its dot ("json", "ini" ...).        */
    /*------------------------------------------------------------------------*/
    public static string Vf_DefaultExtension(int iFormat)
    {
        switch (iFormat)
        {
            case VF_INI:           return("ini");            // INI
            case VF_KLIPPER:       return("cfg");            // Klipper
            case VF_MOONRAKER:     return("conf");           // Moonraker
            case VF_KLIPPERSCREEN: return("conf");           // KlipperScreen
            case VF_REG:           return("reg");            // registry file
            default:               return("json");           // JSON
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_DetectFromPath:                                                     */
    /*                                                                        */
    /* A file's format by its name:                                           */
    /* .json/.jsonc JSON; .ini/.inf INI; .reg REG; .cfg Klipper; .conf by     */
    /* name -- moonraker.conf Moonraker, KlipperScreen.conf KlipperScreen,    */
    /* any other .conf Moonraker.                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath : the file's path or name.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the VF_ id, or VF_DETECT when the name does not say.         */
    /*------------------------------------------------------------------------*/
    public static int Vf_DetectFromPath(string sPath)
    {
        string sExtension;                               // the extension, lower case, with its dot
        string sName;                                    // the file name, lower case

        sExtension = System.IO.Path.GetExtension(sPath).ToLowerInvariant();
        sName = System.IO.Path.GetFileName(sPath).ToLowerInvariant();
        switch (sExtension)
        {
            case ".json":
            case ".jsonc":
                /* JSON, with comments or without. */
                return(VF_JSON);

            case ".ini":
            case ".inf":
                /* Windows INI (setup .inf files are INI too). */
                return(VF_INI);

            case ".reg":
                /* A registry file. */
                return(VF_REG);

            case ".cfg":
                /* Klipper's printer.cfg and its included files. */
                return(VF_KLIPPER);

            case ".conf":
                /* KlipperScreen's own file by name; any other .conf Moonraker. */
                if (sName == "klipperscreen.conf")
                {   /* KlipperScreen.conf. */
                    return(VF_KLIPPERSCREEN);
                }

                /* Any other .conf: Moonraker (K1). */
                return(VF_MOONRAKER);

            default:
                /* The name does not say. */
                return(VF_DETECT);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_DetectFromText:                                                     */
    /*                                                                        */
    /* A file's format by its contents, when its name does not say: a         */
    /* registry file's header means REG; text that reads as JSON is JSON;     */
    /* anything else is INI (which reads any text).                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes : the file's contents.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the VF_ id.                                                  */
    /*------------------------------------------------------------------------*/
    public static int Vf_DetectFromText(byte[] bytes)
    {
        Encoding encoding;                               // the decoded encoding (unused)
        bool bBom;                                       // a byte order mark (unused)
        string sText;                                    // the decoded text
        string sError;                                   // why it is not JSON (unused)

        sText = VoorheesText.Vtx_Decode(bytes, out encoding, out bBom);
        if (sText.StartsWith("Windows Registry Editor Version 5.00",
            StringComparison.Ordinal) || sText.StartsWith("REGEDIT4",
            StringComparison.Ordinal))
        {   /* The registry file header. */
            return(VF_REG);
        }

        if (VoorheesJson.Vjs_Parse(sText, out sError) != null)
        {   /* Valid JSON (comments allowed). */
            return(VF_JSON);
        }

        /* Anything else reads as INI. */
        return(VF_INI);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_Detect:                                                             */
    /*                                                                        */
    /* A file's format: by its name if that says (Vf_DetectFromPath), else    */
    /* by its contents (Vf_DetectFromText).                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath : the file's path.                                           */
    /*     bytes : its contents.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the VF_ id.                                                  */
    /*------------------------------------------------------------------------*/
    public static int Vf_Detect(string sPath, byte[] bytes)
    {
        int iFormat;                                     // the format the name gives

        iFormat = Vf_DetectFromPath(sPath);
        if (iFormat != VF_DETECT)
        {   /* The name says. */
            return(iFormat);
        }

        /* The contents decide. */
        return(Vf_DetectFromText(bytes));
    }

    /*------------------------------------------------------------------------*/
    /* Vf_Name:                                                               */
    /*                                                                        */
    /* A format's name as the user sees it (status bar, messages, menus).     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : a VF_ id.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name; "Unknown" for an id that is not a format.       */
    /*------------------------------------------------------------------------*/
    public static string Vf_Name(int iFormat)
    {
        switch (iFormat)
        {
            case VF_JSON:          return("JSON");                   // JSON / JSONC
            case VF_INI:           return("INI");                    // Windows INI
            case VF_KLIPPER:       return("Klipper config");         // printer.cfg
            case VF_MOONRAKER:     return("Moonraker config");       // moonraker.conf
            case VF_KLIPPERSCREEN: return("KlipperScreen config");   // KlipperScreen.conf
            case VF_REG:           return("Registry file");          // .reg
            default:               return("Unknown");                // not a format id
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_IsAvailable:                                                        */
    /*                                                                        */
    /* Whether a format's module exists yet, so its documents can be made,    */
    /* read and written (the menus grey the others out).                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : a VF_ id.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for JSON, INI and the Klipper family so far.           */
    /*------------------------------------------------------------------------*/
    public static bool Vf_IsAvailable(int iFormat)
    {
        /* Every format has its module. */
        return(iFormat == VF_JSON || iFormat == VF_INI
            || VoorheesCfg.Vcf_IsDialect(iFormat) || iFormat == VF_REG);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_IsLineFormat:                                                       */
    /*                                                                        */
    /* Whether a format is read and written by the line engine                */
    /* (VoorheesLines.cs): INI, the Klipper family and REG.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : a VF_ id.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for a line format with a module.                       */
    /*------------------------------------------------------------------------*/
    static bool Vf_IsLineFormat(int iFormat)
    {
        /* The line formats. */
        return(iFormat == VF_INI || VoorheesCfg.Vcf_IsDialect(iFormat)
            || iFormat == VF_REG);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_FromName:                                                           */
    /*                                                                        */
    /* A format from a word, compared without case: its name ("JSON", "INI",  */
    /* "Klipper config" ...) or a short word (json, ini, klipper, moonraker,  */
    /* klipperscreen, reg) -- the words --format will take (step 7).          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sWord : the word.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the VF_ id, or VF_DETECT for a word that names none.         */
    /*------------------------------------------------------------------------*/
    public static int Vf_FromName(string sWord)
    {
        int iFormat;

        for (iFormat = VF_JSON; iFormat <= VF_REG; iFormat++)
        {
            if (string.Equals(sWord, Vf_Name(iFormat),
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(sWord, Vf_CliName(iFormat),
                StringComparison.OrdinalIgnoreCase))
            {   /* Its name or its short word. */
                return(iFormat);
            }
        }

        /* Not a format. */
        return(VF_DETECT);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_CliName:                                                            */
    /*                                                                        */
    /* A format's short word, for the command line and scripts.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : a VF_ id.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : json ini klipper moonraker klipperscreen reg; "" else.    */
    /*------------------------------------------------------------------------*/
    public static string Vf_CliName(int iFormat)
    {
        switch (iFormat)
        {
            case VF_JSON:          return("json");               // JSON
            case VF_INI:           return("ini");                // INI
            case VF_KLIPPER:       return("klipper");            // printer.cfg
            case VF_MOONRAKER:     return("moonraker");          // moonraker.conf
            case VF_KLIPPERSCREEN: return("klipperscreen");      // KlipperScreen.conf
            case VF_REG:           return("reg");                // .reg
            default:               return("");                   // not a format
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_Load:                                                               */
    /*                                                                        */
    /* A whole file's bytes to a document in a given format: decoded,         */
    /* parsed losslessly, its style detected for generated text.  The         */
    /* document remembers its format (Vd_iFormat).                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ format to read the file as.                      */
    /*     bytes   : the file's contents.                                     */
    /*     sPath   : the file, for the document to remember; may be null.     */
    /*     sError  : set to why the file is not valid in that format (with    */
    /*               line and column), else null.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document, or null on failure.               */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vf_Load(int iFormat, byte[] bytes,
        string sPath, out string sError)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: the JSON module reads it. */
            return(VoorheesJson.Vjs_LoadDocument(bytes, sPath, out sError));
        }

        if (iFormat == VF_INI)
        {   /* INI: the line engine reads it by INI's rules. */
            return(VoorheesIni.Vin_LoadDocument(bytes, sPath, out sError));
        }

        if (VoorheesCfg.Vcf_IsDialect(iFormat))
        {   /* The Klipper family: the line engine reads it by the dialect's rules. */
            return(VoorheesCfg.Vcf_LoadDocument(iFormat, bytes, sPath,
                out sError));
        }

        if (iFormat == VF_REG)
        {   /* REG: the line engine reads it, header first. */
            return(VoorheesReg.Vrg_LoadDocument(bytes, sPath, out sError));
        }

        /* A format whose module is not built yet. */
        sError = Vf_Name(iFormat) + " files can't be opened yet.";
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_SaveBytes:                                                          */
    /*                                                                        */
    /* A document to the bytes of its file, in its own format and encoding.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the file's new contents.                                  */
    /*------------------------------------------------------------------------*/
    public static byte[] Vf_SaveBytes(VoorheesDocument doc)
    {
        switch (doc.Vd_iFormat)
        {
            case VF_JSON:
                /* JSON: the JSON module writes it. */
                return(VoorheesJson.Vjs_SaveBytes(doc));

            case VF_INI:
                /* INI: the line engine writes it. */
                return(VoorheesIni.Vin_SaveBytes(doc));

            case VF_KLIPPER:
            case VF_MOONRAKER:
            case VF_KLIPPERSCREEN:
                /* The Klipper family: the line engine writes it. */
                return(VoorheesCfg.Vcf_SaveBytes(doc));

            case VF_REG:
                /* REG: the line engine writes it. */
                return(VoorheesReg.Vrg_SaveBytes(doc));

            default:
                /* No module can write it: never write a wrong file. */
                throw new InvalidOperationException(Vf_Name(doc.Vd_iFormat)
                    + " files can't be saved yet.");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_Write:                                                              */
    /*                                                                        */
    /* A document as the text of its file (before encoding): what a save      */
    /* would write, for checks and comparisons.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text.                                                 */
    /*------------------------------------------------------------------------*/
    public static string Vf_Write(VoorheesDocument doc)
    {
        switch (doc.Vd_iFormat)
        {
            case VF_JSON:
                /* JSON: the JSON writer. */
                return(VoorheesJson.Vjs_Write(doc));

            case VF_INI:
            case VF_KLIPPER:
            case VF_MOONRAKER:
            case VF_KLIPPERSCREEN:
            case VF_REG:
                /* The line formats: the line engine. */
                return(VoorheesLines.Vln_Write(doc));

            default:
                /* No module: nothing can be written. */
                throw new InvalidOperationException(Vf_Name(doc.Vd_iFormat)
                    + " files can't be written yet.");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ApplyDefaultStyle:                                                  */
    /*                                                                        */
    /* Gives a new document its format's defaults for the style fields only   */
    /* some formats use: the delimiter between key and value, and the marker  */
    /* new comments start with.  Called by Vd_Create; a format's reader then  */
    /* sets what it detects in the file.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document (its format set).                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : Vd_sDelimiter and Vd_sCommentPrefix are set.                */
    /*------------------------------------------------------------------------*/
    public static void Vf_ApplyDefaultStyle(VoorheesDocument doc)
    {
        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: key=value, ";" comments. */
            doc.Vd_sDelimiter = VoorheesIni.VIN_DEFAULTDELIMITER;
            doc.Vd_sCommentPrefix = VoorheesIni.VIN_DEFAULTCOMMENTPREFIX;
            return;
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: "name: value", "#" comments. */
            doc.Vd_sDelimiter = VoorheesCfg.VCF_DEFAULTDELIMITER;
            doc.Vd_sCommentPrefix = VoorheesCfg.VCF_DEFAULTCOMMENTPREFIX;
            return;
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: "name"=value, ";" comments. */
            doc.Vd_sDelimiter = "=";
            doc.Vd_sCommentPrefix = ";";
            return;
        }

        /* JSON (whose own fields are the key and colon gaps): its syntax. */
        doc.Vd_sDelimiter = ":";
        doc.Vd_sCommentPrefix = "//";
    }

    /*------------------------------------------------------------------------*/
    /* Vf_RenderItem:                                                         */
    /*                                                                        */
    /* One entry's text exactly as it is (or would be) written in the file,   */
    /* for the Raw box and the raw tree labels (see the format module's       */
    /* render function for what is included).                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document.                                            */
    /*     lstPath : the entry's position path (not empty).                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text, or "" when the path names no entry.             */
    /*------------------------------------------------------------------------*/
    public static string Vf_RenderItem(VoorheesDocument doc, List<int> lstPath)
    {
        bool bTruncated;                                 // never true without a limit

        /* No limit: the whole entry. */
        return(Vf_RenderItemLimited(doc, lstPath, int.MaxValue, out bTruncated));
    }

    /*------------------------------------------------------------------------*/
    /* Vf_RenderItemLimited:                                                  */
    /*                                                                        */
    /* Vf_RenderItem, stopping after a number of characters, so the start of  */
    /* a huge entry can be shown without writing all of it.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc        : the document.                                         */
    /*     lstPath    : the entry's position path (not empty).                */
    /*     iMaxChars  : most characters wanted (int.MaxValue for no limit).   */
    /*     bTruncated : set to true when the entry is longer than iMaxChars.  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text, at most iMaxChars long.                         */
    /*------------------------------------------------------------------------*/
    public static string Vf_RenderItemLimited(VoorheesDocument doc,
        List<int> lstPath, int iMaxChars, out bool bTruncated)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: the JSON writer renders it. */
            return(VoorheesJson.Vjs_RenderItemLimited(doc, lstPath, iMaxChars,
                out bTruncated));
        }

        if (Vf_IsLineFormat(doc.Vd_iFormat))
        {   /* A line format: the line engine renders its lines. */
            return(VoorheesLines.Vln_RenderItemLimited(doc, lstPath, iMaxChars,
                out bTruncated));
        }

        /* No module: nothing to show. */
        bTruncated = false;
        return("");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ApplyRawEdit:                                                       */
    /*                                                                        */
    /* The entry a Raw edit describes: the Raw box's text parsed strictly in  */
    /* the document's format, as the same kind of entry as the one edited,    */
    /* kept exactly as typed.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : the entry's container (the line formats read the box   */
    /*                 as if under it: an option in a section is valid there, */
    /*                 not at the top of a file).                             */
    /*     oldItem   : the entry being edited.                                */
    /*     sText     : the Raw box's text.                                    */
    /*     sError    : set to why the text is refused (line and column within */
    /*                 the box), else null.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new entry, or null when refused.                */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vf_ApplyRawEdit(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem oldItem, string sText,
        out string sError)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: the JSON parser reads it. */
            return(VoorheesJson.Vjs_ApplyRawEdit(oldItem, sText, out sError));
        }

        if (Vf_IsLineFormat(doc.Vd_iFormat))
        {   /* A line format: the line engine reads it, under its container. */
            return(VoorheesLines.Vln_ApplyRawEdit(doc, container, oldItem, sText,
                out sError));
        }

        /* No module: refuse rather than guess. */
        sError = Vf_Name(doc.Vd_iFormat) + " entries can't be edited as raw "
            + "text yet.";
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_NewDocument:                                                        */
    /*                                                                        */
    /* A new, never-saved document of a format, in that format's default      */
    /* style (JSON: {}).                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ format.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the new document.                               */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vf_NewDocument(int iFormat)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: {}. */
            return(VoorheesJson.Vjs_NewDocument());
        }

        if (iFormat == VF_INI)
        {   /* INI: empty. */
            return(VoorheesIni.Vin_NewDocument());
        }

        if (VoorheesCfg.Vcf_IsDialect(iFormat))
        {   /* The Klipper family: empty, LF. */
            return(VoorheesCfg.Vcf_NewDocument(iFormat));
        }

        if (iFormat == VF_REG)
        {   /* REG: the header, UTF-16 LE BOM, CRLF (R3). */
            return(VoorheesReg.Vrg_NewDocument());
        }

        /* No module: never asked for, since nothing offers it yet. */
        throw new InvalidOperationException("New " + Vf_Name(iFormat)
            + " documents can't be made yet.");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_InitialPath:                                                        */
    /*                                                                        */
    /* What the tree selects when a document is shown, and whether it is      */
    /* opened (JSON: the top value, opened one level).                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document.                                            */
    /*     bExpand : set to true when the entry is to be opened.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the entry's path, or null for none (an empty           */
    /*                 document).                                             */
    /*------------------------------------------------------------------------*/
    public static List<int> Vf_InitialPath(VoorheesDocument doc,
        out bool bExpand)
    {
        List<int> lstPath;                               // the first item's path

        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: the top value. */
            return(VoorheesJson.Vjs_InitialPath(doc, out bExpand));
        }

        /* Any other format: the first item, closed (none when empty). */
        bExpand = false;
        if (doc.Vd_root.Vn_Count() == 0)
        {   /* An empty document: nothing to select. */
            return(null);
        }
        lstPath = new List<int>();
        lstPath.Add(0);

        /* The first top-level item. */
        return(lstPath);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_NewKeyBase:                                                         */
    /*                                                                        */
    /* The key a new keyed entry in a container starts from; the core adds    */
    /* 1, 2, 3 ... until it is free (Vd_UnusedKey).                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (its format).                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the base (JSON "newKey").                                 */
    /*------------------------------------------------------------------------*/
    public static string Vf_NewKeyBase(VoorheesDocument doc,
        VoorheesNode container)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: camelCase. */
            return(VoorheesJson.Vjs_NewKeyBase());
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: "newSection" at the top, "newKey" in a section. */
            return(VoorheesIni.Vin_NewKeyBase(container));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: "new_section" or "new_option". */
            return(VoorheesCfg.Vcf_NewKeyBase(container));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: a key path, or "NewValue". */
            return(VoorheesReg.Vrg_NewKeyBase(container));
        }

        /* No module: a neutral word. */
        return("NewKey");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_NewChild:                                                           */
    /*                                                                        */
    /* Add Child's new item of a kind for a container: a keyed entry with a   */
    /* free key, or an unkeyed one, as the format and container need, holding */
    /* the kind's empty value.                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : the container.                                         */
    /*     iKind     : a value kind the format offers there (Vf_ChildKinds).  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item, not in any document.                  */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vf_NewChild(VoorheesDocument doc,
        VoorheesNode container, int iKind)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: a member in an object, an element in an array. */
            return(VoorheesJson.Vjs_NewChild(doc, container, iKind));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: a section or an entry, with a free name. */
            return(VoorheesIni.Vin_NewChild(doc, container, iKind));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: a section, include or option, with a free name. */
            return(VoorheesCfg.Vcf_NewChild(doc, container, iKind));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: a key or a value, with a free name. */
            return(VoorheesReg.Vrg_NewChild(doc, container, iKind));
        }

        /* No module: never offered (Vf_ChildKinds gives no kinds). */
        throw new InvalidOperationException("No " + Vf_Name(doc.Vd_iFormat)
            + " entries can be made yet.");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_FitItem:                                                            */
    /*                                                                        */
    /* An item made to fit the container it is going into (Insert, Paste),    */
    /* or the reason it cannot go there.  JSON: a member into an array        */
    /* becomes an element, an element into an object becomes a member with a  */
    /* free key, and the document node takes no second value.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : where the item goes.                                   */
    /*     item      : the item (not in any document; not changed).           */
    /*     sError    : set to why it cannot go there, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item to insert, or null when refused.           */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vf_FitItem(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem item, out string sError)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: members, elements and the one top value. */
            return(VoorheesJson.Vjs_FitItem(doc, container, item, out sError));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: keyed entries anywhere, sections at the top. */
            return(VoorheesIni.Vin_FitItem(container, item, out sError));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: options in sections, sections and includes at the top. */
            return(VoorheesCfg.Vcf_FitItem(doc.Vd_iFormat, container, item,
                out sError));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: keys at the top, values in keys. */
            return(VoorheesReg.Vrg_FitItem(container, item, out sError));
        }

        /* No module: as it is. */
        sError = null;
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_EndPosition:                                                        */
    /*                                                                        */
    /* Where an item added at "the end" of a container goes, and how far an   */
    /* insert may go: the end, except where the format keeps something last   */
    /* (the Klipper family's auto-saved block: everything after its header    */
    /* is the block's, so new top-level items go just above it).              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (its format).                             */
    /*     container : the container.                                         */
    /*     sBeyond   : set to why nothing can go after that position, or null */
    /*                 when it is the end.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the last position an item may take.                          */
    /*------------------------------------------------------------------------*/
    public static int Vf_EndPosition(VoorheesDocument doc,
        VoorheesNode container, out string sBeyond)
    {
        int iEnd;                                        // the position

        sBeyond = null;
        if (!VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* Every other format: the end. */
            return(container.Vn_Count());
        }

        iEnd = VoorheesCfg.Vcf_EndPosition(container);
        if (iEnd < container.Vn_Count())
        {   /* Above the auto-saved block. */
            sBeyond = "Nothing can go below the auto-saved block: it must stay "
                + "at the end of the file.";
        }

        /* The last position allowed. */
        return(iEnd);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_CheckItem:                                                          */
    /*                                                                        */
    /* Why an item may not stand at its place in a container, if it may not:  */
    /* a value kind the format does not allow there (asked when a value is    */
    /* set, before the change is made).  JSON allows any value anywhere.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : the item's container.                                  */
    /*     item      : the item as it would be.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is allowed.                   */
    /*------------------------------------------------------------------------*/
    public static string Vf_CheckItem(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem item)
    {
        string sError;                                   // the format's objection

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: the same rule as for inserting (sections at the top only). */
            VoorheesIni.Vin_FitItem(container, item, out sError);
            return(sError);
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: the same rule as for inserting. */
            VoorheesCfg.Vcf_FitItem(doc.Vd_iFormat, container, item, out sError);
            return(sError);
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: the same rule as for inserting. */
            VoorheesReg.Vrg_FitItem(container, item, out sError);
            return(sError);
        }

        /* JSON takes any value anywhere. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_RemoveProblem:                                                      */
    /*                                                                        */
    /* Why an item may not be deleted, if it may not (JSON: the top value).   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : the item's container.                                  */
    /*     item      : the item.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it may be deleted.               */
    /*------------------------------------------------------------------------*/
    public static string Vf_RemoveProblem(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem item)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: not the top value. */
            return(VoorheesJson.Vjs_RemoveProblem(container, item));
        }

        /* No module: anything may go. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_LeadAfterRemove:                                                    */
    /*                                                                        */
    /* When an item is removed, whether the item that will take its place     */
    /* needs a new lead.  In the line formats the line break ending a line    */
    /* belongs to the NEXT item's lead, so removing the FIRST item of the     */
    /* file would leave the next one starting with that line break (and any   */
    /* blank lines that separated them) -- stray blank lines at the top.  The */
    /* next item, which becomes the first, keeps only its indentation.        */
    /* The first item of the Klipper family's auto-saved block is like it:    */
    /* the next one keeps one line break (it follows the header line) and its */
    /* indentation, so no blank "#*#" line is left under the header.          */
    /* (Everywhere else the removed item's own lead takes the line break      */
    /* before it away, which is exactly right.)  JSON needs nothing.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : the container the item is removed from.                */
    /*     iPos      : its position (the item is still there).                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the next item's new lead, or null to leave it alone.      */
    /*------------------------------------------------------------------------*/
    public static string Vf_LeadAfterRemove(VoorheesDocument doc,
        VoorheesNode container, int iPos)
    {
        string sLead;                                    // the next item's lead
        bool bBlock;                                     // the container is an auto-saved block
        int iLastBreak;                                  // its last line break

        bBlock = (container.Vn_iKind == VoorheesCfg.VCF_AUTOSAVE);
        if (!Vf_IsLineFormat(doc.Vd_iFormat)
            || (container.Vn_iKind != VoorheesNode.VN_DOCUMENT && !bBlock)
            || iPos != 0 || container.Vn_Count() < 2)
        {   /* Not the first line of a line-format file (or block) with a line after it. */
            return(null);
        }

        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT && container.Vn_sRaw != null)
        {   /* A file with a header line (REG): the next item keeps the blank line after the header. */
            return(null);
        }

        sLead = container.Vn_lstItems[1].Vi_sLead;
        if (sLead == null)
        {   /* Generated anyway (it will be "" as the first item). */
            return(null);
        }

        iLastBreak = sLead.LastIndexOfAny(new char[] { '\r', '\n' });
        if (iLastBreak < 0)
        {   /* No line break in it: nothing to drop. */
            return(null);
        }

        if (bBlock)
        {   /* In the block: its last line break (both halves of a CRLF) and indentation. */
            if (sLead[iLastBreak] == '\n' && iLastBreak > 0 && sLead[iLastBreak - 1] == '\r')
            {   /* CRLF: from its CR. */
                iLastBreak--;
            }
            return(sLead.Substring(iLastBreak));
        }

        /* Only what follows the last line break: its indentation. */
        return(sLead.Substring(iLastBreak + 1));
    }

    /*------------------------------------------------------------------------*/
    /* Vf_KeyComparer:                                                        */
    /*                                                                        */
    /* How the keys of a container's members compare, for duplicate marks     */
    /* and refusals (JSON exact; INI and REG without case;                    */
    /* CFG section names exact, option names without case).                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat   : the document's VF_ format.                             */
    /*     container : the container whose keys are compared.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     StringComparer : the comparison.                                   */
    /*------------------------------------------------------------------------*/
    public static StringComparer Vf_KeyComparer(int iFormat,
        VoorheesNode container)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: keys are exact strings ("a" and "A" are different keys). */
            return(StringComparer.Ordinal);
        }

        if (iFormat == VF_INI || iFormat == VF_REG)
        {   /* INI and REG: Windows ignores case in key, section and value names (INI 2, section 9). */
            return(StringComparer.OrdinalIgnoreCase);
        }

        if (VoorheesCfg.Vcf_IsDialect(iFormat))
        {   /* The Klipper family: section names exact, option names without case. */
            return(VoorheesCfg.Vcf_KeyComparer(container));
        }

        /* No module: exact. */
        return(StringComparer.Ordinal);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ImportItem:                                                         */
    /*                                                                        */
    /* An item copied from a document of ANOTHER format, made into one of     */
    /* this document's format:                                                */
    /* a comment keeps its words in this format's syntax; a single value      */
    /* keeps its key and becomes this format's closest kind (JSON string,     */
    /* number or boolean -> INI or Klipper-family text, JSON null -> an INI   */
    /* key alone or empty Klipper-family text; INI or Klipper-family text ->  */
    /* JSON string, a key alone -> JSON null), if the target's value rules    */
    /* allow it; a container is refused.                                      */
    /* An item of the same format is returned as it is.                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc           : the document pasted into.                          */
    /*     iSourceFormat : the format the item was copied from.               */
    /*     container     : where it goes.                                     */
    /*     item          : the copied item (not changed).                     */
    /*     sError        : set to why it cannot be converted, else null.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item in this format (new, its layout to be      */
    /*                    generated), or null when refused.                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vf_ImportItem(VoorheesDocument doc,
        int iSourceFormat, VoorheesNode container, VoorheesItem item,
        out string sError)
    {
        VoorheesNode node;                               // the value in this format
        string sRaw;                                     // a comment in this format
        string sText;                                    // a value's text
        bool bNoValue;                                   // the value is JSON null or an INI key alone

        sError = null;
        if (iSourceFormat == doc.Vd_iFormat)
        {   /* The same format: nothing to convert. */
            return(item);
        }

        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment: its words, in this format's syntax. */
            sRaw = Vf_MakeComment(doc, container, container.Vn_Count(),
                Vf_CommentText(iSourceFormat, item.Vi_sComment), false,
                out sError);
            if (sRaw == null)
            {   /* The words cannot be a comment here. */
                return(null);
            }
            return(VoorheesItem.Vi_NewComment(sRaw));
        }

        if (item.Vi_node.Vn_IsContainer())
        {   /* Objects, arrays and sections do not convert (yet). */
            sError = "Only single values and comments can be pasted between "
                + "different file types.";
            return(null);
        }

        /* The value: the target format's closest kind.  A value with no     */
        /* value at all (JSON null, an INI key alone) is "no value"; any     */
        /* other has its text (JSON strings, numbers and booleans, INI and   */
        /* Klipper-family text).                                             */
        bNoValue = (item.Vi_node.Vn_iKind == VoorheesJson.VJS_NULL
            || item.Vi_node.Vn_iKind == VoorheesIni.VIN_KEYONLY
            || item.Vi_node.Vn_iKind == VoorheesReg.VRG_DELETEVALUE);
        sText = item.Vi_node.Vn_sText;
        if (doc.Vd_iFormat == VF_INI)
        {   /* Into INI: text, or a key alone for no value. */
            if (bNoValue)
            {   /* No value. */
                node = VoorheesNode.Vn_NewLeaf(VoorheesIni.VIN_KEYONLY, "");
            }
            else
            {   /* The value's text, if it can be an INI value. */
                sError = VoorheesIni.Vin_ValueProblem(sText);
                if (sError != null)
                {   /* Several lines, or spaces at an end. */
                    return(null);
                }
                node = VoorheesNode.Vn_NewLeaf(VoorheesIni.VIN_TEXT, sText);
            }
        }
        else if (doc.Vd_iFormat == VF_JSON)
        {   /* Into JSON: a string, or null for no value. */
            if (bNoValue)
            {   /* No value. */
                node = VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_NULL, "null");
            }
            else
            {   /* The text, exactly. */
                node = VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_STRING, sText);
            }
        }
        else if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* Into the Klipper family: text ("" for no value), if it can be a value there. */
            if (bNoValue)
            {   /* No value: an empty one. */
                sText = "";
            }
            node = VoorheesCfg.Vcf_MakeValue(doc.Vd_iFormat, sText, out sError);
            if (node == null)
            {   /* Not a valid value in this dialect. */
                return(null);
            }
        }
        else if (doc.Vd_iFormat == VF_REG)
        {   /* Into REG (section 10): a whole number that fits is a DWORD, several lines a Multi-String, anything else a String. */
            if (bNoValue)
            {   /* No value: an empty string. */
                sText = "";
            }
            if (item.Vi_node.Vn_iKind == VoorheesJson.VJS_NUMBER
                && VoorheesReg.Vrg_ValueProblem(VoorheesReg.VRG_DWORD, sText) == null)
            {   /* A number that fits 32 bits. */
                node = VoorheesReg.Vrg_MakeValue(VoorheesReg.VRG_DWORD, sText, out sError);
            }
            else if (sText.IndexOf('\n') >= 0 || sText.IndexOf('\r') >= 0)
            {   /* Several lines: a list. */
                node = VoorheesReg.Vrg_MakeValue(VoorheesReg.VRG_MULTISZ, sText, out sError);
            }
            else
            {   /* A string. */
                node = VoorheesReg.Vrg_MakeValue(VoorheesReg.VRG_SZ, sText, out sError);
            }
            if (node == null)
            {   /* Not a valid value. */
                return(null);
            }
        }
        else
        {   /* No module to convert into. */
            sError = Vf_Name(doc.Vd_iFormat) + " values can't be pasted yet.";
            return(null);
        }

        if (item.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* A keyed value keeps its key. */
            return(VoorheesItem.Vi_NewMember(item.Vi_sKey, node));
        }

        /* An unkeyed value (a JSON array element). */
        return(VoorheesItem.Vi_NewElement(node));
    }

    /*------------------------------------------------------------------------*/
    /* Vf_DuplicateMessage:                                                   */
    /*                                                                        */
    /* Why a key is refused when another member of the container already has  */
    /* it (Voorhees never creates a repeated key), in the format's words:     */
    /* JSON's objects and keys, INI's sections and keys.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (its format).                             */
    /*     container : the container.                                         */
    /*     item      : the member that would take the key.                    */
    /*     sKey      : the key.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the message.                                              */
    /*------------------------------------------------------------------------*/
    public static string Vf_DuplicateMessage(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem item, string sKey)
    {
        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: sections, includes and options. */
            return(VoorheesCfg.Vcf_DuplicateMessage(item, sKey));
        }

        if (doc.Vd_iFormat == VF_REG && item.Vi_IsValue() && item.Vi_node.Vn_IsContainer())
        {   /* A registry key. */
            return("This file already lists the key \"" + sKey
                + "\" (paths are compared without case).");
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* A registry value. */
            return("This key already has a value named \"" + sKey
                + "\" (names are compared without case).");
        }

        if (doc.Vd_iFormat == VF_INI && item.Vi_IsValue()
            && item.Vi_node.Vn_IsContainer())
        {   /* An INI section name. */
            return("This file already has a section named \"" + sKey
                + "\" (names are compared without case).");
        }

        if (doc.Vd_iFormat == VF_INI
            && container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* An INI key before the first section. */
            return("The top of this file already has a key named \"" + sKey
                + "\" (keys are compared without case).");
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* An INI key in a section. */
            return("This section already has a key named \"" + sKey
                + "\" (keys are compared without case).");
        }

        /* JSON: an object's key. */
        return("This object already has a key named \"" + sKey + "\".");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_KeyProblem:                                                         */
    /*                                                                        */
    /* Why a keyed entry may not take a new key, by the format's key syntax   */
    /* (duplicates are the core's check, with Vf_KeyComparer).  Called only   */
    /* for a key that differs from the entry's current one.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : the entry's container.                                 */
    /*     item      : the entry (its kind and value as they are).            */
    /*     sNewKey   : the proposed key.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when the key is acceptable.           */
    /*------------------------------------------------------------------------*/
    public static string Vf_KeyProblem(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem item, string sNewKey)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: anything but empty. */
            return(VoorheesJson.Vjs_KeyProblem(sNewKey));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: what would not read back as the same key or name. */
            return(VoorheesIni.Vin_KeyProblem(item, sNewKey));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: what would not read back as the same name. */
            return(VoorheesCfg.Vcf_KeyProblem(doc.Vd_iFormat, item, sNewKey));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: one line; key paths under a hive. */
            return(VoorheesReg.Vrg_KeyProblem(item, sNewKey));
        }

        /* No module: refuse. */
        return(Vf_Name(doc.Vd_iFormat) + " keys can't be changed yet.");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_IsSearchable:                                                       */
    /*                                                                        */
    /* Whether a plain value's text is looked at by Find and Replace (JSON    */
    /* null is not: "null" is not text the file holds as a value).            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document (its format).                                  */
    /*     node : a plain value.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when its text is searched.                             */
    /*------------------------------------------------------------------------*/
    public static bool Vf_IsSearchable(VoorheesDocument doc, VoorheesNode node)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: everything but null. */
            return(node.Vn_iKind != VoorheesJson.VJS_NULL);
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: text; a key alone has none. */
            return(node.Vn_iKind == VoorheesIni.VIN_TEXT);
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: every value but one to delete. */
            return(node.Vn_iKind != VoorheesReg.VRG_DELETEVALUE);
        }

        /* No module: everything. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ItemName:                                                           */
    /*                                                                        */
    /* The name a value entry is labelled with in the tree (JSON: the key,    */
    /* the array index, or "root").                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc           : the document (its format).                         */
    /*     container     : the entry's container.                             */
    /*     iPos          : its position.                                      */
    /*     iElementIndex : an element's index if the caller is counting them  */
    /*                     as it goes; -1 to have it counted.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name, as stored (the tree makes it one line).         */
    /*------------------------------------------------------------------------*/
    public static string Vf_ItemName(VoorheesDocument doc,
        VoorheesNode container, int iPos, int iElementIndex)
    {
        VoorheesItem item;                               // the entry

        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: key, index or root. */
            return(VoorheesJson.Vjs_ItemName(container, iPos, iElementIndex));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: the path or name; "(Default)" for "@". */
            return(VoorheesReg.Vrg_ItemName(container.Vn_lstItems[iPos]));
        }

        /* No module: the key if it has one. */
        item = container.Vn_lstItems[iPos];
        if (item.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* Keyed: its key. */
            return(item.Vi_sKey);
        }

        /* Unkeyed: no name. */
        return("");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_LabelBrackets:                                                      */
    /*                                                                        */
    /* The brackets a container's tree label puts round its name (JSON {}     */
    /* and []), or false for a plain value, labelled name, separator, value.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*     iKind   : the value's kind.                                        */
    /*     sOpen   : set to the opening bracket, or null.                     */
    /*     sClose  : set to the closing bracket, or null.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the label is bracketed.                           */
    /*------------------------------------------------------------------------*/
    public static bool Vf_LabelBrackets(int iFormat, int iKind,
        out string sOpen, out string sClose)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: objects and arrays. */
            return(VoorheesJson.Vjs_LabelBrackets(iKind, out sOpen, out sClose));
        }

        if (iFormat == VF_INI && iKind == VoorheesIni.VIN_SECTION)
        {   /* INI: [section]. */
            sOpen = "[";
            sClose = "]";
            return(true);
        }

        if (VoorheesCfg.Vcf_IsDialect(iFormat))
        {   /* The Klipper family: [section], [include path], the auto-saved block. */
            return(VoorheesCfg.Vcf_LabelBrackets(iFormat, iKind, out sOpen,
                out sClose));
        }

        if (iFormat == VF_REG && (iKind == VoorheesReg.VRG_KEY
            || iKind == VoorheesReg.VRG_DELETEKEY))
        {   /* REG: [path], or [-path] for a key to delete. */
            sOpen = "[";
            if (iKind == VoorheesReg.VRG_DELETEKEY)
            {   /* Deleted. */
                sOpen = "[-";
            }
            sClose = "]";
            return(true);
        }

        /* No module: no brackets. */
        sOpen = null;
        sClose = null;
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_LabelSeparator:                                                     */
    /*                                                                        */
    /* What a plain value's tree label puts between its name and its value    */
    /* (": " for JSON and the Klipper family, " = " for INI and REG).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the separator.                                            */
    /*------------------------------------------------------------------------*/
    public static string Vf_LabelSeparator(int iFormat)
    {
        if (iFormat == VF_INI || iFormat == VF_REG)
        {   /* Key = value formats. */
            return(" = ");
        }

        /* JSON, and the Klipper family's "name: value". */
        return(": ");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_LabelValue:                                                         */
    /*                                                                        */
    /* A plain value as its tree label shows it (JSON: its text; REG will     */
    /* show numbers in hex and decimal).                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document (its format).                                  */
    /*     node : the plain value.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text (the tree makes it one line); null when the      */
    /*              label is the name alone (an INI key on its own).          */
    /*------------------------------------------------------------------------*/
    public static string Vf_LabelValue(VoorheesDocument doc, VoorheesNode node)
    {
        if (doc.Vd_iFormat == VF_INI && node.Vn_iKind == VoorheesIni.VIN_KEYONLY)
        {   /* A key on its own: no value part. */
            return(null);
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: numbers in hex and decimal, "(delete)", or the text. */
            return(VoorheesReg.Vrg_LabelValue(node));
        }

        /* The value's own text. */
        return(node.Vn_DisplayValue());
    }

    /*------------------------------------------------------------------------*/
    /* Vf_LabelLineComment:                                                   */
    /*                                                                        */
    /* An entry's line comment as its tree label shows it, in grey, after the */
    /* value (JSON "// words"; line formats will show it as written).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*     sRaw    : the comment as stored.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text (the tree makes it one line).                    */
    /*------------------------------------------------------------------------*/
    public static string Vf_LabelLineComment(int iFormat, string sRaw)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: "// words". */
            return(VoorheesJson.Vjs_LabelLineComment(sRaw));
        }

        /* No module: as written. */
        return(sRaw);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_OpenWarnings:                                                       */
    /*                                                                        */
    /* What the user should be told when a file opens, each a message of its  */
    /* own (JSON: repeated keys, which many programs reduce to the last       */
    /* copy).  The counts that need a walk of the whole document are made on  */
    /* the reading worker and passed in.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document just read.                             */
    /*     sFileName    : its file's name (no folder), for the messages.      */
    /*     iExtraCopies : repeated-key copies in it (Vd_ExtraCopies).         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the messages, in order (empty for none).            */
    /*------------------------------------------------------------------------*/
    public static List<string> Vf_OpenWarnings(VoorheesDocument doc,
        string sFileName, int iExtraCopies)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: repeated keys. */
            return(VoorheesJson.Vjs_OpenWarnings(sFileName, iExtraCopies));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: repeated keys and sections (Windows reads the first). */
            return(VoorheesIni.Vin_OpenWarnings(sFileName, iExtraCopies));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: repeats, a damaged block, KlipperScreen's "%". */
            return(VoorheesCfg.Vcf_OpenWarnings(doc, sFileName, iExtraCopies));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: repeats (the last copy wins), keys not under a hive. */
            return(VoorheesReg.Vrg_OpenWarnings(doc, sFileName, iExtraCopies));
        }

        /* No module: nothing to say. */
        return(new List<string>());
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ValidateWarnings:                                                   */
    /*                                                                        */
    /* The one-line warnings --validate prints about a file that is valid but */
    /* worth a second look: JSON's repeated keys (most programs keep the      */
    /* last copy) and comments (JSONC, refused by standard readers); INI's    */
    /* repeated keys or sections (Windows reads the first copy) and keys      */
    /* alone on a line.                                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document read.                                  */
    /*     iExtraCopies : repeated-key copies (Vd_ExtraCopies).               */
    /*     bHasComments : the document holds comments.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the warnings (empty for none).                      */
    /*------------------------------------------------------------------------*/
    public static List<string> Vf_ValidateWarnings(VoorheesDocument doc,
        int iExtraCopies, bool bHasComments)
    {
        List<string> lstWarnings;                        // the warnings
        int iKeyOnly;                                    // INI keys alone on a line

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: repeats, a damaged block, KlipperScreen's "%". */
            return(VoorheesCfg.Vcf_ValidateWarnings(doc, iExtraCopies));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: repeats, keys not under a hive. */
            return(VoorheesReg.Vrg_ValidateWarnings(doc, iExtraCopies));
        }

        lstWarnings = new List<string>();
        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: repeats, and keys without a value. */
            if (iExtraCopies > 0)
            {   /* Something repeats. */
                lstWarnings.Add(iExtraCopies.ToString(CultureInfo.InvariantCulture)
                    + " repeated key(s) or section(s) (compared without case); "
                    + "Windows reads the first copy");
            }

            iKeyOnly = VoorheesIni.Vin_CountKeyOnly(doc.Vd_root);
            if (iKeyOnly > 0)
            {   /* Lines with a key and no "=". */
                lstWarnings.Add(iKeyOnly.ToString(CultureInfo.InvariantCulture)
                    + " key(s) alone on a line, with no \"=\" or value");
            }
            return(lstWarnings);
        }

        if (iExtraCopies > 0)
        {   /* JSON: a key repeats within an object. */
            lstWarnings.Add(iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " repeated key(s) within an object; many programs keep only "
                + "the last copy");
        }

        if (bHasComments)
        {   /* JSON: comments are not standard. */
            lstWarnings.Add("contains comments (JSONC); standard JSON readers "
                + "will refuse it");
        }

        /* Every warning, or none. */
        return(lstWarnings);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_DescribeStyle:                                                      */
    /*                                                                        */
    /* The format-specific part of --validate --verbose's summary: JSON's     */
    /* indentation; INI's delimiter and comment marker.                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document read.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "4-space indent", or "\"=\" delimiter, \";\"         */
    /*              comments".                                                */
    /*------------------------------------------------------------------------*/
    public static string Vf_DescribeStyle(VoorheesDocument doc)
    {
        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: delimiter, marker, continuation indent. */
            return(VoorheesCfg.Vcf_DescribeStyle(doc));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: which header. */
            return(VoorheesReg.Vrg_DescribeStyle(doc));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: what new lines will look like. */
            return("\"" + doc.Vd_sDelimiter + "\" delimiter, \""
                + doc.Vd_sCommentPrefix + "\" comments");
        }

        if (doc.Vd_sIndentUnit == "\t")
        {   /* JSON: one tab per level. */
            return("tab indent");
        }

        /* JSON: a run of spaces per level. */
        return(doc.Vd_sIndentUnit.Length.ToString(CultureInfo.InvariantCulture)
            + "-space indent");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_EditWarning:                                                        */
    /*                                                                        */
    /* A warning the format has about a change just made, undone or redone,   */
    /* which the editor shows at most once per document (JSON: the first      */
    /* comment makes the file JSONC; the Klipper family will warn about the   */
    /* first edit inside an auto-saved block).                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document.                                       */
    /*     step         : the step just made, undone or redone.               */
    /*     bHadComments : the document held a comment before it.              */
    /*     bHasComments : it holds one now.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the message, or null for none.                            */
    /*------------------------------------------------------------------------*/
    public static string Vf_EditWarning(VoorheesDocument doc,
        VoorheesEditStep step, bool bHadComments, bool bHasComments)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: the first comment. */
            return(VoorheesJson.Vjs_EditWarning(bHadComments, bHasComments));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: the first change inside the auto-saved block. */
            return(VoorheesCfg.Vcf_EditWarning(doc, step));
        }

        /* No module: nothing to say. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_IncludePath:                                                        */
    /*                                                                        */
    /* The file an entry includes, for the right-click "Open Included File"   */
    /* (the Klipper family's [include path] lines, decision K3): its full     */
    /* path or wildcard pattern, read from the document's folder.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document.                                               */
    /*     item : the entry.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the path or pattern, or null when the entry includes      */
    /*              nothing (or the document has no file to read it from).    */
    /*------------------------------------------------------------------------*/
    public static string Vf_IncludePath(VoorheesDocument doc, VoorheesItem item)
    {
        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: an include's file. */
            return(VoorheesCfg.Vcf_IncludePath(doc, item));
        }

        /* No other format includes files. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_NameQuestion:                                                       */
    /*                                                                        */
    /* Whether Add Child asks for a new entry's name before adding it, and    */
    /* how (a VoorheesNameQuestion: words, a default, and for some a choice   */
    /* from a list): the Klipper family's includes ask for the file;          */
    /* registry keys ask for the hive from a list and the path under it       */
    /* (Vrg_NameQuestion).  Every other kind gets a free placeholder name,    */
    /* edited                                                                 */
    /* afterwards in the Key box.  The answer becomes the name through        */
    /* Vf_NameFromAnswer.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc   : the document (for a free default name).                    */
    /*     iKind : the kind being added.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNameQuestion : the question, or null when a placeholder    */
    /*                            name will do.                               */
    /*------------------------------------------------------------------------*/
    public static VoorheesNameQuestion Vf_NameQuestion(VoorheesDocument doc, int iKind)
    {
        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat) && iKind == VoorheesCfg.VCF_INCLUDE)
        {   /* An include: its file. */
            return(VoorheesNameQuestion.Vnq_CreateText("Add Include",
                "File to include (from this file's folder; * and ? allowed):", ""));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* A registry key (or a key to delete): hive and path; values need nothing. */
            return(VoorheesReg.Vrg_NameQuestion(doc, iKind));
        }

        /* A placeholder name will do. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_RenameQuestion:                                                     */
    /*                                                                        */
    /* Whether Rename Key asks for the new name in a question of the          */
    /* format's own rather than the plain box with the key in it: registry    */
    /* keys ask for the hive from a drop-down and the path under it, starting */
    /* at the key as it is (Vrg_RenameQuestion).  The answer becomes the name */
    /* through Vf_NameFromAnswer, as for Add.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document.                                               */
    /*     item : the member being renamed.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNameQuestion : the question, or null for the plain box.    */
    /*------------------------------------------------------------------------*/
    public static VoorheesNameQuestion Vf_RenameQuestion(VoorheesDocument doc,
        VoorheesItem item)
    {
        if (doc.Vd_iFormat == VF_REG)
        {   /* A registry key: hive and path; a value's name: the plain box. */
            return(VoorheesReg.Vrg_RenameQuestion(item));
        }

        /* The plain box. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_NameFromAnswer:                                                     */
    /*                                                                        */
    /* The name an answer to a Vf_NameQuestion gives: the text as typed,      */
    /* except where the question had a choice -- a registry key's hive and    */
    /* path joined (Vrg_NameFromAnswer).                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat  : the document's VF_ format.                              */
    /*     question : the question asked.                                     */
    /*     iChoice  : the choice made (an index into Vnq_arrChoices), or -1.  */
    /*     sText    : the text typed.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name to give the new entry.                           */
    /*------------------------------------------------------------------------*/
    public static string Vf_NameFromAnswer(int iFormat, VoorheesNameQuestion question,
        int iChoice, string sText)
    {
        if (iFormat == VF_REG && question.Vnq_arrChoices != null
            && iChoice >= 0 && iChoice < question.Vnq_arrChoices.Length)
        {   /* A registry key: the hive chosen and the path typed. */
            return(VoorheesReg.Vrg_NameFromAnswer(question.Vnq_arrChoices[iChoice], sText));
        }

        /* The text as typed. */
        return(sText);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_CommentsMakeNonStandard:                                            */
    /*                                                                        */
    /* Whether comments make a file of this format non-standard, so the       */
    /* status bar says "Contains comments" while it has any (JSON: comments   */
    /* make it JSONC).  The line formats have comments as a matter of course. */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for JSON.                                              */
    /*------------------------------------------------------------------------*/
    public static bool Vf_CommentsMakeNonStandard(int iFormat)
    {
        /* Only JSON has no comments in its standard. */
        return(iFormat == VF_JSON);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_FinalizeItem:                                                       */
    /*                                                                        */
    /* The format's last word on an item an editor action is about to put     */
    /* into the document (VoorheesDocument.Vd_Finalize): whatever depends on  */
    /* the format is worked out afresh -- JSON's odd-comment flag -- and      */
    /* format details are normalised.  The item is not in a document yet, so  */
    /* it may be changed in place.                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (its format).                             */
    /*     container : the container the item is going into.                  */
    /*     item      : the item.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item to put in.                                 */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vf_FinalizeItem(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem item)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: the odd-comment flag. */
            return(VoorheesJson.Vjs_FinalizeItem(item));
        }

        if (Vf_IsLineFormat(doc.Vd_iFormat))
        {   /* A line format: every comment is a whole line or a line's tail, all editable. */
            item.Vi_bOddComments = false;
            return(item);
        }

        /* No module: as it is. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ResetLayout:                                                        */
    /*                                                                        */
    /* Clears a copied item's layout -- and that of everything inside it --   */
    /* so it is written afresh in the document's style, keeping its keys,     */
    /* values and comments (Paste: the copy's old layout belonged to where it */
    /* was copied from).  ONLY for a copy that is not in a document.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document it will go into (its format).                  */
    /*     item : the copy.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the copy's layout fields are reset.                         */
    /*------------------------------------------------------------------------*/
    public static void Vf_ResetLayout(VoorheesDocument doc, VoorheesItem item)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: whitespace gaps, leads and closing text. */
            VoorheesJson.Vjs_ResetLayout(item);
        }
        else if (doc.Vd_iFormat == VF_INI)
        {   /* INI: leads, delimiters, trailing spaces. */
            VoorheesIni.Vin_ResetLayout(item);
        }
        else if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: leads, gaps, values regenerated in this file's style. */
            VoorheesCfg.Vcf_ResetLayout(item);
        }
        else if (doc.Vd_iFormat == VF_REG)
        {   /* REG: leads, gaps, names and values regenerated in regedit's form. */
            VoorheesReg.Vrg_ResetLayout(item);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_MakeComment:                                                        */
    /*                                                                        */
    /* A NEW comment, from its words, for a place in a container: the line    */
    /* comment of the item at iPos, or a comment item at iPos (iPos past the  */
    /* last item for one added at the end).  The format picks the syntax      */
    /* (JSON: "// words", or a block comment where the line goes on after     */
    /* it).                                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document (its format).                          */
    /*     container    : the container.                                      */
    /*     iPos         : the item, or where the comment item goes.           */
    /*     sText        : the words.                                          */
    /*     bLineComment : true for an item's line comment, false for a        */
    /*                    comment item.                                       */
    /*     sError       : set to why the words cannot be a comment, else      */
    /*                    null.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment with its markers, or null when refused.       */
    /*------------------------------------------------------------------------*/
    public static string Vf_MakeComment(VoorheesDocument doc,
        VoorheesNode container, int iPos, string sText, bool bLineComment,
        out string sError)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: line or block comment. */
            return(VoorheesJson.Vjs_MakeCommentAt(container, iPos, sText,
                bLineComment, out sError));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: the file's marker and the words, one line. */
            return(VoorheesIni.Vin_MakeComment(doc.Vd_sCommentPrefix, sText,
                out sError));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat) || doc.Vd_iFormat == VF_REG)
        {   /* The Klipper family and REG: the file's marker and the words, one line. */
            return(VoorheesCfg.Vcf_MakeComment(doc.Vd_sCommentPrefix, sText,
                out sError));
        }

        /* No module: refuse. */
        sError = Vf_Name(doc.Vd_iFormat) + " comments can't be made yet.";
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_RemakeComment:                                                      */
    /*                                                                        */
    /* An existing comment with new words, in its own style (unchanged words  */
    /* give it back exactly as written), for its place in a container.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document (its format).                          */
    /*     container    : the container.                                      */
    /*     iPos         : the item whose line comment it is, or the comment   */
    /*                    item.                                               */
    /*     sOldRaw      : the comment as stored.                              */
    /*     sText        : its new words.                                      */
    /*     bLineComment : true for an item's line comment, false for a        */
    /*                    comment item.                                       */
    /*     sError       : set to why the words cannot be a comment, else      */
    /*                    null.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vf_RemakeComment(VoorheesDocument doc,
        VoorheesNode container, int iPos, string sOldRaw, string sText,
        bool bLineComment, out string sError)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: keep its style. */
            return(VoorheesJson.Vjs_RemakeCommentAt(container, iPos, sOldRaw,
                sText, bLineComment, out sError));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: its own marker, one line. */
            return(VoorheesIni.Vin_RemakeComment(doc.Vd_sCommentPrefix,
                sOldRaw, sText, out sError));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat) || doc.Vd_iFormat == VF_REG)
        {   /* The Klipper family and REG: its own marker, one line. */
            return(VoorheesCfg.Vcf_RemakeComment(doc.Vd_sCommentPrefix,
                sOldRaw, sText, out sError));
        }

        /* No module: refuse. */
        sError = Vf_Name(doc.Vd_iFormat) + " comments can't be edited yet.";
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_HasLineComment:                                                     */
    /*                                                                        */
    /* Whether an entry can carry a comment on its line (the Comment box).    */
    /* JSON: every value can; comment entries are edited through their own    */
    /* words instead.  (Line formats without end-of-line comments -- INI,     */
    /* REG -- will say no for their entries.)                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (its format).                             */
    /*     container : the entry's container.                                 */
    /*     item      : the entry.                                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it has a line comment to show and edit.           */
    /*------------------------------------------------------------------------*/
    public static bool Vf_HasLineComment(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem item)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: any value. */
            return(item.Vi_IsValue());
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: only a section's header line (entries have no inline comments: INI 1). */
            return(item.Vi_IsValue() && item.Vi_node.Vn_IsContainer());
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: header lines, and options where the dialect has inline comments. */
            return(VoorheesCfg.Vcf_HasLineComment(doc.Vd_iFormat, item));
        }

        /* No module: none. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_CommentText:                                                        */
    /*                                                                        */
    /* A stored comment's words without its markers, as the tree, the Value   */
    /* box and the Comment box show them and as search looks at them.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*     sRaw    : the comment as stored, markers included (or null).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the words; "" for null.                                   */
    /*------------------------------------------------------------------------*/
    public static string Vf_CommentText(int iFormat, string sRaw)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: // and block comments. */
            return(VoorheesJson.Vjs_CommentText(sRaw));
        }

        if (iFormat == VF_INI)
        {   /* INI: ";" or "#" lines. */
            return(VoorheesIni.Vin_CommentText(sRaw));
        }

        if (VoorheesCfg.Vcf_IsDialect(iFormat) || iFormat == VF_REG)
        {   /* The Klipper family ("#" or ";") and REG (";") comments. */
            return(VoorheesCfg.Vcf_CommentText(sRaw));
        }

        /* No module: the comment as it is. */
        if (sRaw == null)
        {   /* No comment: no words. */
            return("");
        }
        return(sRaw);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_KindName:                                                           */
    /*                                                                        */
    /* A value kind's name as the Type box and messages show it.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*     iKind   : a kind id of that format.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name; "Value" for a kind the format does not name.    */
    /*------------------------------------------------------------------------*/
    public static string Vf_KindName(int iFormat, int iKind)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: its own names. */
            return(VoorheesJson.Vjs_KindName(iKind));
        }

        if (iFormat == VF_INI)
        {   /* INI: Text, Key Only, Section. */
            return(VoorheesIni.Vin_KindName(iKind));
        }

        if (VoorheesCfg.Vcf_IsDialect(iFormat))
        {   /* The Klipper family: Text, Section, Include, Auto-saved block. */
            return(VoorheesCfg.Vcf_KindName(iKind));
        }

        if (iFormat == VF_REG)
        {   /* REG: String, DWORD ... Key, Delete Key. */
            return(VoorheesReg.Vrg_KindName(iKind));
        }

        /* Anything else: a neutral word. */
        return("Value");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ChildKinds:                                                         */
    /*                                                                        */
    /* The value kinds Add Child offers in a container, in menu order (the    */
    /* menu adds Comment... itself).                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (its format).                             */
    /*     container : the container the child would go into.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds (a new list; empty when the container takes  */
    /*                 comments only).                                        */
    /*------------------------------------------------------------------------*/
    public static List<int> Vf_ChildKinds(VoorheesDocument doc,
        VoorheesNode container)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: any value in an object or array. */
            return(VoorheesJson.Vjs_ChildKinds(container));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: entries anywhere, sections at the top. */
            return(VoorheesIni.Vin_ChildKinds(container));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: sections and includes at the top, options in sections. */
            return(VoorheesCfg.Vcf_ChildKinds(doc.Vd_iFormat, container));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: keys at the top, values in keys. */
            return(VoorheesReg.Vrg_ChildKinds(container));
        }

        /* No module: nothing can be added. */
        return(new List<int>());
    }

    /*------------------------------------------------------------------------*/
    /* Vf_PanelKinds:                                                         */
    /*                                                                        */
    /* The kinds the Type box offers for a value entry, in order (the box     */
    /* adds Comment itself, for comment entries).                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document (its format).                               */
    /*     lstPath : the value entry's position path.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds (a new list).                                */
    /*------------------------------------------------------------------------*/
    public static List<int> Vf_PanelKinds(VoorheesDocument doc,
        List<int> lstPath)
    {
        VoorheesItem item;                               // the entry

        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: every value kind, wherever the value is. */
            return(VoorheesJson.Vjs_PanelKinds());
        }

        item = doc.Vd_ItemAt(lstPath);
        if (doc.Vd_iFormat == VF_INI && item != null && item.Vi_IsValue())
        {   /* INI: Text or Key Only for an entry; a section stays one. */
            return(VoorheesIni.Vin_PanelKinds(item.Vi_node));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat) && item != null
            && item.Vi_IsValue())
        {   /* The Klipper family: Text for an option; a container stays what it is. */
            return(VoorheesCfg.Vcf_PanelKinds(item.Vi_node));
        }

        if (doc.Vd_iFormat == VF_REG && item != null && item.Vi_IsValue())
        {   /* REG: the value kinds, or Key / Delete Key. */
            return(VoorheesReg.Vrg_PanelKinds(item.Vi_node));
        }

        /* No module, or no value there: no kinds. */
        return(new List<int>());
    }

    /*------------------------------------------------------------------------*/
    /* Vf_KindHasText:                                                        */
    /*                                                                        */
    /* Whether a kind's value is typed into the Value box (which is greyed    */
    /* out for the kinds that have nothing to type, such as JSON null and     */
    /* the containers).                                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*     iKind   : a kind id of that format.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the Value box is used.                            */
    /*------------------------------------------------------------------------*/
    public static bool Vf_KindHasText(int iFormat, int iKind)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: strings, numbers, booleans. */
            return(VoorheesJson.Vjs_KindHasText(iKind));
        }

        if (iFormat == VF_INI)
        {   /* INI: only Text has a value to type. */
            return(iKind == VoorheesIni.VIN_TEXT);
        }

        if (VoorheesCfg.Vcf_IsDialect(iFormat))
        {   /* The Klipper family: only Text has a value to type. */
            return(iKind == VoorheesCfg.VCF_TEXT);
        }

        if (iFormat == VF_REG)
        {   /* REG: every value but one to delete; keys have none. */
            return(iKind != VoorheesReg.VRG_DELETEVALUE && iKind != VoorheesReg.VRG_KEY
                && iKind != VoorheesReg.VRG_DELETEKEY);
        }

        /* No module: every value of a line format is text. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_IsBooleanKind:                                                      */
    /*                                                                        */
    /* Whether a kind is edited with the Boolean checkbox instead of the      */
    /* Value box (JSON's true / false only: no other format has a boolean).   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*     iKind   : a kind id of that format.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for the checkbox.                                      */
    /*------------------------------------------------------------------------*/
    public static bool Vf_IsBooleanKind(int iFormat, int iKind)
    {
        /* Only JSON has booleans. */
        return(iFormat == VF_JSON && iKind == VoorheesJson.VJS_BOOLEAN);
    }

    /*------------------------------------------------------------------------*/
    /* Value colour roles: one                                                */
    /* shared set for every format; each value kind takes one of them.        */
    /*------------------------------------------------------------------------*/

    public const int VF_ROLE_TEXT = 0;                   // strings and text: JSON string, INI / Klipper text, REG strings
    public const int VF_ROLE_NUMBER = 1;                 // JSON number, REG DWORD / QWORD
    public const int VF_ROLE_BOOLEAN = 2;                // JSON true / false
    public const int VF_ROLE_NULL = 3;                   // no value: JSON null, an INI key alone, a REG value to delete
    public const int VF_ROLE_BINARY = 4;                 // REG Binary and Other Hex
    public const int VF_ROLECOUNT = 5;                   // how many value roles there are

    /*------------------------------------------------------------------------*/
    /* Vf_ValueRole:                                                          */
    /*                                                                        */
    /* The colour role a plain value's kind takes in the tree (decision C2:   */
    /* values are coloured per type).  Kind ids are unique across formats,    */
    /* so the kind alone decides.                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a value kind of any format.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : a VF_ROLE_ value (VF_ROLE_TEXT for anything not listed).     */
    /*------------------------------------------------------------------------*/
    public static int Vf_ValueRole(int iKind)
    {
        switch (iKind)
        {
            case VoorheesJson.VJS_NUMBER:
            case VoorheesReg.VRG_DWORD:
            case VoorheesReg.VRG_QWORD:
                /* Numbers. */
                return(VF_ROLE_NUMBER);

            case VoorheesJson.VJS_BOOLEAN:
                /* true / false. */
                return(VF_ROLE_BOOLEAN);

            case VoorheesJson.VJS_NULL:
            case VoorheesIni.VIN_KEYONLY:
            case VoorheesReg.VRG_DELETEVALUE:
                /* No value. */
                return(VF_ROLE_NULL);

            case VoorheesReg.VRG_BINARY:
            case VoorheesReg.VRG_HEXOTHER:
                /* Bytes. */
                return(VF_ROLE_BINARY);

            default:
                /* Text of every kind. */
                return(VF_ROLE_TEXT);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ValueFromPanel:                                                     */
    /*                                                                        */
    /* The value the edit panel describes -- the kind chosen in the Type box  */
    /* with the Value box's text or the checkbox -- for the panel's Save.     */
    /* Read by the format's own rules (JSON: Vjs_ValueFromPanel).             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc      : the document (its format).                              */
    /*     iKind    : the kind chosen.                                        */
    /*     sText    : the Value box's text (CRLF line breaks).                */
    /*     bChecked : the Boolean checkbox.                                   */
    /*     oldNode  : the value being replaced.                               */
    /*     sError   : set to why the value is refused, else null.             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value (possibly oldNode itself, unchanged), or  */
    /*                    null when refused.                                  */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vf_ValueFromPanel(VoorheesDocument doc,
        int iKind, string sText, bool bChecked, VoorheesNode oldNode,
        out string sError)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: its kinds' rules. */
            return(VoorheesJson.Vjs_ValueFromPanel(iKind, sText, bChecked,
                oldNode, out sError));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: one-line text, a key alone, or the section itself. */
            return(VoorheesIni.Vin_ValueFromPanel(iKind, sText, oldNode,
                out sError));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: text (one line or several), or the container itself. */
            return(VoorheesCfg.Vcf_ValueFromPanel(doc.Vd_iFormat, iKind, sText,
                oldNode, out sError));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: a typed value, or a key / key to delete. */
            return(VoorheesReg.Vrg_ValueFromPanel(iKind, sText, oldNode, out sError));
        }

        /* No module: refuse. */
        sError = Vf_Name(doc.Vd_iFormat) + " values can't be edited yet.";
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_NewValue:                                                           */
    /*                                                                        */
    /* An empty value of a kind, as Add Child and a Type box change make one  */
    /* (JSON: "", 0, false, null, {} or []).                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*     iKind   : a kind id of that format.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value, not in any document.                 */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vf_NewValue(int iFormat, int iKind)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: its defaults. */
            return(VoorheesJson.Vjs_NewDefault(iKind));
        }

        if (iFormat == VF_INI)
        {   /* INI: empty text, a key alone, an empty section. */
            return(VoorheesIni.Vin_NewValue(iKind));
        }

        if (VoorheesCfg.Vcf_IsDialect(iFormat))
        {   /* The Klipper family: empty text, or an empty container. */
            return(VoorheesCfg.Vcf_NewValue(iKind));
        }

        if (iFormat == VF_REG)
        {   /* REG: empty strings and bytes, zero numbers, an empty key. */
            return(VoorheesReg.Vrg_NewValue(iKind));
        }

        /* No module: never happens for a document that was opened. */
        throw new InvalidOperationException("No values can be made for "
            + Vf_Name(iFormat) + " yet.");
    }

    /*------------------------------------------------------------------------*/
    /* Vf_ConvertText:                                                        */
    /*                                                                        */
    /* A new value of the SAME kind as an old one, from edited text (in-place */
    /* editing and Replace keep a value's kind; the Type box changes it).     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document (its format).                               */
    /*     oldNode : the value being replaced.                                */
    /*     sText   : the new text.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value, or null when the text is not valid   */
    /*                    for the old value's kind.                           */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vf_ConvertText(VoorheesDocument doc,
        VoorheesNode oldNode, string sText)
    {
        if (doc.Vd_iFormat == VF_JSON)
        {   /* JSON: string, number, boolean, null rules. */
            return(VoorheesJson.Vjs_ConvertText(oldNode, sText));
        }

        if (doc.Vd_iFormat == VF_INI)
        {   /* INI: one-line text (a key alone gains its "="). */
            return(VoorheesIni.Vin_ConvertText(oldNode, sText));
        }

        if (VoorheesCfg.Vcf_IsDialect(doc.Vd_iFormat))
        {   /* The Klipper family: text by the dialect's value rules. */
            return(VoorheesCfg.Vcf_ConvertText(doc.Vd_iFormat, oldNode, sText));
        }

        if (doc.Vd_iFormat == VF_REG)
        {   /* REG: text valid for the value's type. */
            return(VoorheesReg.Vrg_ConvertText(oldNode, sText));
        }

        /* No module: refuse. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vf_IsSingleComment:                                                    */
    /*                                                                        */
    /* Whether a stored comment is exactly one comment, which the Comment and */
    /* Value boxes can edit (several together are Raw-only).                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the document's VF_ format.                               */
    /*     sRaw    : the comment as stored.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for one comment.                                       */
    /*------------------------------------------------------------------------*/
    public static bool Vf_IsSingleComment(int iFormat, string sRaw)
    {
        if (iFormat == VF_JSON)
        {   /* JSON: one // comment, or one block comment. */
            return(VoorheesJson.Vjs_IsSingleComment(sRaw));
        }

        if (Vf_IsLineFormat(iFormat))
        {   /* A line format: a comment is one line, always one comment. */
            return(sRaw != null);
        }

        /* No module: never editable outside Raw. */
        return(false);
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesNameQuestion                                                       */
/*                                                                            */
/* What a format asks before it adds an entry whose name matters too much for */
/* a placeholder (VoorheesFormat.Vf_NameQuestion): the dialog's title, the    */
/* text box's prompt and starting text, and optionally a choice from a fixed  */
/* list above it (a drop-down: the registry's hives).  The editor shows it    */
/* (MainForm.DLG_AskName); the format turns the answer into the name          */
/* (Vf_NameFromAnswer).  Format-agnostic: nothing here knows a format.        */
/*----------------------------------------------------------------------------*/
class VoorheesNameQuestion
{
    public string Vnq_sTitle;                            // the dialog's title, e.g. "Add Key"
    public string Vnq_sChoiceLabel;                      // the drop-down's label, or null for no choice
    public string[] Vnq_arrChoices;                      // the drop-down's entries, or null for no choice
    public int Vnq_iChoice;                              // the entry chosen at first (-1 with no choice)
    public string Vnq_sPrompt;                           // the text box's label
    public string Vnq_sText;                             // the text box's starting text

    /*------------------------------------------------------------------------*/
    /* Vnq_CreateText:                                                        */
    /*                                                                        */
    /* A question answered by text alone.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sTitle  : the dialog's title.                                      */
    /*     sPrompt : the text box's label.                                    */
    /*     sText   : its starting text.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNameQuestion : the question, with no choice.               */
    /*------------------------------------------------------------------------*/
    public static VoorheesNameQuestion Vnq_CreateText(string sTitle, string sPrompt,
        string sText)
    {
        VoorheesNameQuestion question;                   // the new question

        question = new VoorheesNameQuestion();
        question.Vnq_sTitle = sTitle;
        question.Vnq_sChoiceLabel = null;
        question.Vnq_arrChoices = null;
        question.Vnq_iChoice = -1;
        question.Vnq_sPrompt = sPrompt;
        question.Vnq_sText = sText;

        /* Text only. */
        return(question);
    }

    /*------------------------------------------------------------------------*/
    /* Vnq_CreateChoice:                                                      */
    /*                                                                        */
    /* A question answered by a choice from a list (a drop-down, nothing else */
    /* can be typed there) and text.                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sTitle       : the dialog's title.                                 */
    /*     sChoiceLabel : the drop-down's label.                              */
    /*     arrChoices   : its entries (copied).                               */
    /*     iChoice      : the entry chosen at first.                          */
    /*     sPrompt      : the text box's label.                               */
    /*     sText        : its starting text.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNameQuestion : the question.                               */
    /*------------------------------------------------------------------------*/
    public static VoorheesNameQuestion Vnq_CreateChoice(string sTitle, string sChoiceLabel,
        string[] arrChoices, int iChoice, string sPrompt, string sText)
    {
        VoorheesNameQuestion question;                   // the new question

        question = Vnq_CreateText(sTitle, sPrompt, sText);
        question.Vnq_sChoiceLabel = sChoiceLabel;
        question.Vnq_arrChoices = (string[])arrChoices.Clone();
        question.Vnq_iChoice = iChoice;

        /* Choice and text. */
        return(question);
    }
}
