/*-----------------------------------------------------------------------------*/
/* VoorheesCfg.cs                                                              */
/*                                                                             */
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/*-----------------------------------------------------------------------------*/
/* The Klipper family's config files: Klipper                                  */
/* (printer.cfg), Moonraker (moonraker.conf) and KlipperScreen                 */
/* (KlipperScreen.conf).  All three read their files with Python's             */
/* configparser, each with its own settings, so they share this module and     */
/* differ where the design's table says:                                       */
/*                                                                             */
/*                 inline comments              section name    repeats        */
/*   Klipper       "#" anywhere; ";" after      up to the LAST  allowed        */
/*                 whitespace                   "]"                            */
/*   Moonraker     spaces then "#" or ";"       up to the FIRST error          */
/*                 (" \#" and " \;" escape a    "]"                            */
/*                 literal one)                                                */
/*   KlipperScreen none                         up to the LAST  error          */
/*                                              "]"                            */
/*                                                                             */
/* Shared (configparser): delimiters "=" and ":" (the first wins); full-line   */
/* comments "#" / ";" after any indentation; option names compare without      */
/* case, section names with it; a line indented deeper than its option line    */
/* continues the value (the line engine finds them: VoorheesLines.cs           */
/* Vln_TakeContinuations), blank and comment lines inside included.            */
/* Sources read for these rules (each program's own source):                   */
/* Klipper klippy/configfile.py, Moonraker moonraker/confighelper.py           */
/* _parse_file, KlipperScreen ks_includes/config.py, Python's configparser.    */
/*                                                                             */
/* STRICT where the programs themselves refuse a file, with line and column    */
/* (Vcf_LineProblem): an option before the first section, a line that is not   */
/* a section, an option or a comment, an option with no name, an option        */
/* under an [include ...] line (Klipper and Moonraker), an include with no     */
/* file name.                                                                  */
/*                                                                             */
/* Model, beyond the line engine's (VoorheesLines.cs header):                  */
/*     option  : VI_MEMBER, Vi_sKey the name as written; Vi_sKeyGap the        */
/*               spaces, delimiter and spaces; value a VCF_TEXT leaf whose     */
/*               Vn_sRaw is the value exactly as written -- through the last   */
/*               continuation line, blank and comment lines inside included    */
/*               -- and whose Vn_sText is the Value box's text (below); the    */
/*               first line's trailing spaces go to Vi_sValueGap and its       */
/*               inline comment to Vi_sEolGap / Vi_sEolComment, which the      */
/*               writer puts back at the end of the FIRST line.                */
/*     section : VI_MEMBER, value a VCF_SECTION container; text after the      */
/*               "]" is ignored by all three programs: a comment there goes    */
/*               to Vi_sOpenGap + Vi_sOpenComment, anything else to            */
/*               Vi_sValueGap.                                                 */
/*     include : an [include path] line (decision K3): VI_MEMBER keyed by the  */
/*               path, value a VCF_INCLUDE container (comments only, or        */
/*               options too for KlipperScreen, which reads them as a section  */
/*               of that name); Vi_sKeyRaw the bracket text when it is not     */
/*               "include " + path.                                            */
/*     block   : Klipper's SAVE_CONFIG ("#*#") and KlipperScreen's ("#~#")     */
/*               auto-saved block at the end of the file (decision K2):        */
/*               VI_MEMBER keyed "SAVE_CONFIG" / "auto-saved", Vi_sKeyRaw its  */
/*               header lines as written, value a VCF_AUTOSAVE container whose */
/*               items are the block's SECTIONS read with the prefix           */
/*               removed, and whose Vn_sClose is the block's text after the    */
/*               last of them, prefixes included (Vcf_ParseBlock,              */
/*               Vcf_WriteBlock).  A block that would not read back exactly,   */
/*               or that its program would call corrupted, stays plain         */
/*               comment lines, and opening the file says so.                  */
/*                                                                             */
/* THE VALUE BOX.  A one-line value shows as the program reads it (Moonraker's */
/* escapes decoded; writing encodes them again).  A multi-line value -- a      */
/* G-code macro -- shows as source: the first line's value, then every line    */
/* after it (blank lines and comment lines included, so editing keeps them)    */
/* with the value lines' common indentation removed; writing indents them      */
/* again by the file's own continuation indent (Vd_sIndentUnit).               */
/*                                                                             */
/* Class:                                                                      */
/*     VoorheesCfg (Vcf_) : static functions only; nothing is kept between     */
/*                          calls, so any of them may run on a worker thread.  */
/*-----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*----------------------------------------------------------------------------*/
/* VoorheesCfg                                                                */
/*                                                                            */
/* Klipper / Moonraker / KlipperScreen lines and rules.                       */
/*----------------------------------------------------------------------------*/
static class VoorheesCfg
{
    /*------------------------------------------------------------------------*/
    /* The Klipper family's kinds of node, in its range (section 4).          */
    /*------------------------------------------------------------------------*/

    public const int VCF_TEXT = 30;                      // an option's value, one line or several
    public const int VCF_SECTION = 31;                   // a [section] and its lines
    public const int VCF_INCLUDE = 32;                   // an [include path] line (comments only inside, options too for KlipperScreen)
    public const int VCF_AUTOSAVE = 33;                  // the #*# SAVE_CONFIG / #~# auto-saved block

    /*------------------------------------------------------------------------*/
    /* Defaults for generated text.                                           */
    /*------------------------------------------------------------------------*/

    public const string VCF_DEFAULTDELIMITER = ": ";     // between name and value: what the programs' own examples use
    public const string VCF_DEFAULTCOMMENTPREFIX = "#";  // the comment marker the programs' own files use
    public const string VCF_DEFAULTINDENT = "  ";        // continuation lines of a multi-line value, beyond the option's own indentation
    const string VCF_NEWOPTIONBASE = "new_option";       // a new option's name, before 1, 2, 3 ... make it free
    const string VCF_NEWSECTIONBASE = "new_section";     // a new section's name, likewise
    const string VCF_NEWINCLUDECFG = "new_file.cfg";     // a new Klipper include's file
    const string VCF_NEWINCLUDECONF = "new_file.conf";   // a new Moonraker / KlipperScreen include's file
    const string VCF_INCLUDEWORD = "include ";           // a header naming "include " + a path is an include (all three programs)

    /*------------------------------------------------------------------------*/
    /* The auto-saved blocks (decision K2).  Klipper: configfile.py           */
    /* AUTOSAVE_HEADER, three lines; every later line "#*#" alone or "#*# "   */
    /* + text.  KlipperScreen: config.py do_not_edit_line, then "#~#" lines;  */
    /* it writes "#~#" after the marker and at the end, "#~# " + text for     */
    /* every line between (an empty line too).                                */
    /*------------------------------------------------------------------------*/

    const string VCF_SAVECONFIGLINE1 = "#*# <---------------------- SAVE_CONFIG ---------------------->";        // Klipper header, line 1
    const string VCF_SAVECONFIGLINE2 = "#*# DO NOT EDIT THIS BLOCK OR BELOW. The contents are auto-generated.";  // line 2
    const string VCF_SAVECONFIGLINE3 = "#*#";                                                                    // line 3
    const string VCF_SAVECONFIGPREFIX = "#*#";                                                                   // Klipper's block line prefix
    const string VCF_KSMARKER = "#~# --- Do not edit below this line. This section is auto generated --- #~#";   // KlipperScreen's marker line
    const string VCF_KSPREFIX = "#~#";                                                                           // KlipperScreen's block line prefix
    const string VCF_SAVECONFIGKEY = "SAVE_CONFIG";                                                              // the Klipper block's name in the tree
    const string VCF_KSBLOCKKEY = "auto-saved";                                                                  // the KlipperScreen block's name in the tree
    const string VCF_BLOCKDELIMITER = " = ";                                                                     // configparser.write's delimiter, for entries generated in a block
    const string VCF_BLOCKINDENT = "\t";                                                                         // configparser.write's continuation indent, likewise

    /*------------------------------------------------------------------------*/
    /* Vcf_IsDialect:                                                         */
    /*                                                                        */
    /* Whether a format id is one of this module's three dialects.            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : a VoorheesFormat.VF_ id.                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for Klipper, Moonraker and KlipperScreen.              */
    /*------------------------------------------------------------------------*/
    public static bool Vcf_IsDialect(int iFormat)
    {
        /* The three configparser dialects. */
        return(iFormat == VoorheesFormat.VF_KLIPPER
            || iFormat == VoorheesFormat.VF_MOONRAKER
            || iFormat == VoorheesFormat.VF_KLIPPERSCREEN);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_ProgramName:                                                       */
    /*                                                                        */
    /* The program that reads a dialect's files, for messages.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : "Klipper", "Moonraker" or "KlipperScreen".                */
    /*------------------------------------------------------------------------*/
    static string Vcf_ProgramName(int iFormat)
    {
        if (iFormat == VoorheesFormat.VF_MOONRAKER)
        {   /* moonraker.conf. */
            return("Moonraker");
        }

        if (iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* KlipperScreen.conf. */
            return("KlipperScreen");
        }

        /* printer.cfg. */
        return("Klipper");
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_IsSpace:                                                           */
    /*                                                                        */
    /* Whether a character is a space or a tab (the only whitespace a line    */
    /* holds; Moonraker expands tabs to spaces before it looks).              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     c : the character.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for ' ' and '\t'.                                      */
    /*------------------------------------------------------------------------*/
    static bool Vcf_IsSpace(char c)
    {
        /* Space or tab. */
        return(c == ' ' || c == '\t');
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_IsBlank:                                                           */
    /*                                                                        */
    /* Whether a text is empty or spaces and tabs only.                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when there is nothing else in it.                      */
    /*------------------------------------------------------------------------*/
    static bool Vcf_IsBlank(string s)
    {
        /* Nothing left once spaces and tabs go. */
        return(s.Trim(' ', '\t').Length == 0);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_IsCommentLine:                                                     */
    /*                                                                        */
    /* Whether a line (or a line of a value) is a full-line comment: its      */
    /* first non-space character is "#" or ";" (configparser's                */
    /* comment_prefixes, and Moonraker's own check).                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the line.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for a comment line.                                    */
    /*------------------------------------------------------------------------*/
    static bool Vcf_IsCommentLine(string s)
    {
        string sTrim;                                    // the line without its indentation

        sTrim = s.TrimStart(' ', '\t');

        /* "#" or ";" first. */
        return(sTrim.Length > 0 && (sTrim[0] == '#' || sTrim[0] == ';'));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_ParseLine:                                                         */
    /*                                                                        */
    /* One line as an item, by a dialect's rules:                             */
    /*     o a comment line ("#" or ";" first);                               */
    /*     o a section header: "[" first, the name up to the last "]" before  */
    /*       the line's inline comment (Klipper, KlipperScreen) or the first  */
    /*       "]" (Moonraker); a name "include " + path is an include;         */
    /*     o an option: name, then the first "=" or ":" before the inline     */
    /*       comment, then the value up to that comment.                      */
    /* A line that is none of these (no delimiter) comes back as an option    */
    /* with no key gap, which Vcf_LineProblem reports.                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat  : VF_KLIPPER, VF_MOONRAKER or VF_KLIPPERSCREEN.           */
    /*     sContent : the line from its first non-space character to its line */
    /*                break (not empty).                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item; its lead is the line engine's to set.     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vcf_ParseLine(int iFormat, string sContent)
    {
        VoorheesItem item;                               // the item made
        VoorheesNode node;                               // an option's value
        int iEffective;                                  // where the line's inline comment starts (or its end)
        int iClose;                                      // a section's "]"
        int iDelimiter;                                  // an option's "=" or ":"
        int iValueStart;                                 // where the value starts
        int iValueEnd;                                   // where it ends (before trailing spaces)
        string sKey;                                     // the option name
        string sRaw;                                     // the value as written

        if (sContent[0] == '#' || sContent[0] == ';')
        {   /* A comment line, kept whole. */
            return(VoorheesItem.Vi_NewComment(sContent));
        }

        /* What the program reads of the line: up to its inline comment.   */
        /* (From the second character: the first is not a marker here.)    */
        iEffective = Vcf_InlineCommentStart(iFormat, sContent, 1);
        if (iEffective < 0)
        {   /* No inline comment: the whole line. */
            iEffective = sContent.Length;
        }

        if (sContent[0] == '[')
        {   /* Perhaps a section header: find its "]" by the dialect's rule. */
            if (iFormat == VoorheesFormat.VF_MOONRAKER)
            {   /* Moonraker: the name ends at the first "]". */
                iClose = sContent.IndexOf(']', 0, iEffective);
            }
            else
            {   /* Klipper, KlipperScreen: greedy, up to the last "]". */
                iClose = sContent.LastIndexOf(']', iEffective - 1, iEffective);
            }

            if (iClose > 1)
            {   /* A header with a name of at least one character. */
                return(Vcf_MakeHeader(sContent, iClose));
            }
        }

        /* An option: the first delimiter before the comment splits it. */
        iDelimiter = sContent.IndexOfAny(new char[] { '=', ':' }, 0, iEffective);
        if (iDelimiter < 0)
        {   /* No delimiter: kept whole, with no key gap (Vcf_LineProblem reports it). */
            return(VoorheesItem.Vi_NewMember(sContent,
                VoorheesNode.Vn_NewLeaf(VCF_TEXT, "")));
        }

        sKey = sContent.Substring(0, iDelimiter).TrimEnd(' ', '\t');
        iValueStart = iDelimiter + 1;
        while (iValueStart < iEffective && Vcf_IsSpace(sContent[iValueStart]))
        {
            iValueStart++;
        }

        /* The value runs to the comment, trailing spaces apart. */
        iValueEnd = iEffective;
        while (iValueEnd > iValueStart && Vcf_IsSpace(sContent[iValueEnd - 1]))
        {
            iValueEnd--;
        }
        sRaw = sContent.Substring(iValueStart, iValueEnd - iValueStart);

        /* The value: as written (raw), and as the program reads it (text:   */
        /* Moonraker's " \#" escapes decoded).                               */
        node = VoorheesNode.Vn_NewLeaf(VCF_TEXT, Vcf_DecodeLine(iFormat, sRaw,
            Vcf_IsSpace(sContent[iValueStart - 1])));
        node.Vn_sRaw = sRaw;

        item = VoorheesItem.Vi_NewMember(sKey, node);
        item.Vi_sKeyGap = sContent.Substring(sKey.Length, iValueStart - sKey.Length);
        if (iEffective < sContent.Length)
        {   /* An inline comment: the spaces before it, then it. */
            item.Vi_sEolGap = sContent.Substring(iValueEnd, iEffective - iValueEnd);
            item.Vi_sEolComment = sContent.Substring(iEffective);
        }
        else if (iValueEnd < sContent.Length)
        {   /* Trailing spaces. */
            item.Vi_sValueGap = sContent.Substring(iValueEnd);
        }

        /* The option as written. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_MakeHeader:                                                        */
    /*                                                                        */
    /* A section or include item from a header line: the name between "["     */
    /* and the "]" found; "include " + path makes an include keyed by the     */
    /* path (spaces round it trimmed, as all three programs do).  Text after  */
    /* the "]" is ignored by the programs: a comment there (after any spaces) */
    /* is the header's comment, anything else is kept as written.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sContent : the header line (from its "[").                         */
    /*     iClose   : the "]" that ends the name.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the header item, its container empty.               */
    /*------------------------------------------------------------------------*/
    static VoorheesItem Vcf_MakeHeader(string sContent, int iClose)
    {
        VoorheesItem item;                               // the item made
        VoorheesNode node;                               // its container
        string sName;                                    // the text between the brackets
        string sPath;                                    // an include's path
        string sAfter;                                   // the text after the "]"
        string sSpaces;                                  // its leading spaces
        string sRest;                                    // what follows them

        sName = sContent.Substring(1, iClose - 1);
        if (sName.StartsWith(VCF_INCLUDEWORD, StringComparison.Ordinal))
        {   /* [include path]: keyed by the path; the bracket text kept when generation would differ. */
            sPath = sName.Substring(VCF_INCLUDEWORD.Length).Trim(' ', '\t');
            node = VoorheesNode.Vn_NewContainer(VCF_INCLUDE);
            node.Vn_sClose = "";
            item = VoorheesItem.Vi_NewMember(sPath, node);
            if (sName != VCF_INCLUDEWORD + sPath)
            {   /* Spacing generation would not give back. */
                item.Vi_sKeyRaw = sName;
            }
        }
        else
        {   /* A section, named exactly as written. */
            node = VoorheesNode.Vn_NewContainer(VCF_SECTION);
            node.Vn_sClose = "";
            item = VoorheesItem.Vi_NewMember(sName, node);
        }

        /* After the "]": a comment, or other text kept as it is. */
        sAfter = sContent.Substring(iClose + 1);
        sSpaces = sAfter.Substring(0, sAfter.Length - sAfter.TrimStart(' ', '\t').Length);
        sRest = sAfter.Substring(sSpaces.Length);
        if (sRest.Length > 0 && (sRest[0] == '#' || sRest[0] == ';'))
        {   /* A comment on the header line. */
            item.Vi_sOpenGap = sSpaces;
            item.Vi_sOpenComment = sRest;
        }
        else if (sAfter.Length > 0)
        {   /* Spaces or other text: kept exactly. */
            item.Vi_sValueGap = sAfter;
        }

        /* The header, empty until its lines are read. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_InlineCommentStart:                                                */
    /*                                                                        */
    /* Where a dialect's inline comment starts in a line: Klipper -- any "#"  */
    /* (its own pre-pass cuts there), or a ";" after a space or tab           */
    /* (configparser's inline prefixes); Moonraker -- a "#" or ";" after a    */
    /* space or tab (a backslash before it is an escape, so it does not       */
    /* count); KlipperScreen -- never.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat  : the dialect.                                            */
    /*     sContent : the line.                                               */
    /*     iFrom    : where to start looking.                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the comment marker's position, or -1 for none.               */
    /*------------------------------------------------------------------------*/
    static int Vcf_InlineCommentStart(int iFormat, string sContent, int iFrom)
    {
        bool bAfterSpace;                                // the character before is a space or tab
        int i;

        if (iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* No inline comments at all. */
            return(-1);
        }

        for (i = iFrom; i < sContent.Length; i++)
        {
            bAfterSpace = (i > 0 && Vcf_IsSpace(sContent[i - 1]));
            if (iFormat == VoorheesFormat.VF_KLIPPER && sContent[i] == '#')
            {   /* Klipper cuts the line at any "#". */
                return(i);
            }

            if ((sContent[i] == '#' || sContent[i] == ';') && bAfterSpace)
            {   /* A marker after whitespace (Klipper ";", Moonraker both). */
                return(i);
            }
        }

        /* No inline comment. */
        return(-1);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_DecodeLine:                                                        */
    /*                                                                        */
    /* A one-line value as its program reads it: Moonraker turns " \#" and    */
    /* " \;" (a space or tab, a backslash, the marker) into the marker        */
    /* without the backslash -- its escape for a literal "#" or ";" where it  */
    /* would start a comment.  Klipper and KlipperScreen read the value as    */
    /* written.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat     : the dialect.                                         */
    /*     sRaw        : the value as written.                                */
    /*     bAfterSpace : the character before the value is a space or tab     */
    /*                   (so an escape at its very start counts).             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the value's text.                                         */
    /*------------------------------------------------------------------------*/
    static string Vcf_DecodeLine(int iFormat, string sRaw, bool bAfterSpace)
    {
        StringBuilder sb;                                // the decoded text
        bool bSpaceBefore;                               // the character before i is whitespace
        int i;

        if (iFormat != VoorheesFormat.VF_MOONRAKER || sRaw.IndexOf('\\') < 0)
        {   /* No escapes to decode. */
            return(sRaw);
        }

        sb = new StringBuilder();
        for (i = 0; i < sRaw.Length; i++)
        {
            if (i == 0)
            {   /* At the start: what is before the value counts. */
                bSpaceBefore = bAfterSpace;
            }
            else
            {   /* Inside: the character before. */
                bSpaceBefore = Vcf_IsSpace(sRaw[i - 1]);
            }

            if (sRaw[i] == '\\' && bSpaceBefore && i + 1 < sRaw.Length
                && (sRaw[i + 1] == '#' || sRaw[i + 1] == ';'))
            {   /* An escape: the backslash goes, the marker stays. */
                continue;
            }
            sb.Append(sRaw[i]);
        }

        /* The value as Moonraker reads it. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_EncodeLine:                                                        */
    /*                                                                        */
    /* The reverse of Vcf_DecodeLine, for a value Voorhees writes: in a       */
    /* Moonraker file a "#" or ";" after a space or tab (or at the start of   */
    /* the value, after a gap ending in one) gets a backslash before it, so   */
    /* Moonraker does not read it as a comment.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat     : the dialect.                                         */
    /*     sText       : the value's text.                                    */
    /*     bAfterSpace : what is written before the value ends in a space or  */
    /*                   tab.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the value as it is to be written.                         */
    /*------------------------------------------------------------------------*/
    static string Vcf_EncodeLine(int iFormat, string sText, bool bAfterSpace)
    {
        StringBuilder sb;                                // the encoded text
        bool bSpaceBefore;                               // the character before i is whitespace
        int i;

        if (iFormat != VoorheesFormat.VF_MOONRAKER)
        {   /* Only Moonraker has escapes. */
            return(sText);
        }

        sb = new StringBuilder();
        for (i = 0; i < sText.Length; i++)
        {
            if (i == 0)
            {   /* At the start: what is before the value counts. */
                bSpaceBefore = bAfterSpace;
            }
            else
            {   /* Inside: the character before. */
                bSpaceBefore = Vcf_IsSpace(sText[i - 1]);
            }

            if ((sText[i] == '#' || sText[i] == ';') && bSpaceBefore)
            {   /* Would start a comment: escape it. */
                sb.Append('\\');
            }
            sb.Append(sText[i]);
        }

        /* The value as Moonraker must see it written. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_AddContinuation:                                                   */
    /*                                                                        */
    /* Adds an option's continuation lines (VoorheesLines.cs                  */
    /* Vln_TakeContinuations) to its value: the raw value becomes the first   */
    /* line's value plus the continuation text exactly as written (the first  */
    /* line's trailing spaces and inline comment stay in the item, and the    */
    /* writer puts them back at the end of the first line); the Value box's   */
    /* text is the first line's text and the continuation as source           */
    /* (Vcf_DedentContinuation).                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item  : the option.                                                */
    /*     sMore : the continuation text (it starts with a line break).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the option's value is replaced by its multi-line form.      */
    /*------------------------------------------------------------------------*/
    public static void Vcf_AddContinuation(VoorheesItem item, string sMore)
    {
        VoorheesNode node;                               // the new value
        string sFirstRaw;                                // the first line's value as written

        sFirstRaw = item.Vi_node.Vn_sRaw;
        if (sFirstRaw == null)
        {   /* A line with no delimiter (refused anyway): nothing written before. */
            sFirstRaw = "";
        }

        node = VoorheesNode.Vn_NewLeaf(VCF_TEXT, item.Vi_node.Vn_sText
            + Vcf_DedentContinuation(sMore));
        node.Vn_sRaw = sFirstRaw + sMore;
        item.Vi_node = node;
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_DedentContinuation:                                                */
    /*                                                                        */
    /* Continuation text as the Value box shows it: every line after the      */
    /* option line -- blank lines as empty lines, comment lines kept -- with  */
    /* the smallest indentation of the VALUE lines (not blank, not comments)  */
    /* removed from each (from a comment line only as much as it has), every  */
    /* line break as LF.                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sMore : the continuation text (starting with a line break).        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : "\n" + line, for each line.                               */
    /*------------------------------------------------------------------------*/
    static string Vcf_DedentContinuation(string sMore)
    {
        string[] arrLines;                               // the lines; [0] is the option line's (empty) rest
        StringBuilder sb;                                // the result
        int iMin;                                        // smallest indentation of the value lines
        int iIndent;                                     // a line's indentation
        int i;

        arrLines = sMore.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        /* The value lines' common indentation. */
        iMin = int.MaxValue;
        for (i = 1; i < arrLines.Length; i++)
        {
            if (Vcf_IsBlank(arrLines[i]) || Vcf_IsCommentLine(arrLines[i]))
            {   /* Blank or a comment: does not set the indentation. */
                continue;
            }
            iIndent = arrLines[i].Length - arrLines[i].TrimStart(' ', '\t').Length;
            iMin = Math.Min(iMin, iIndent);
        }
        if (iMin == int.MaxValue)
        {   /* No value line (never: a continuation ends with one). */
            iMin = 0;
        }

        sb = new StringBuilder();
        for (i = 1; i < arrLines.Length; i++)
        {
            sb.Append('\n');
            if (Vcf_IsBlank(arrLines[i]))
            {   /* A blank line: an empty line of the value. */
                continue;
            }
            iIndent = arrLines[i].Length - arrLines[i].TrimStart(' ', '\t').Length;
            sb.Append(arrLines[i].Substring(Math.Min(iMin, iIndent)));
        }

        /* The lines, dedented. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_LineProblem:                                                       */
    /*                                                                        */
    /* Why a line read from a file (or the Raw box) is refused, if it is, by  */
    /* what its program itself refuses:                                       */
    /*     o a line with no "=" or ":" that is not a section or a comment;    */
    /*     o an option with no name ("= value");                              */
    /*     o an option before the first section;                              */
    /*     o an option under an [include ...] line (Klipper and Moonraker     */
    /*       read the lines after an include as if no section came before);   */
    /*     o an include with no file name.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     item    : the item the line made.                                  */
    /*     under   : the container of the section, include or block it falls  */
    /*               under, or null before the first.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason (to follow "Line L, column C: "), or null.     */
    /*------------------------------------------------------------------------*/
    public static string Vcf_LineProblem(int iFormat, VoorheesItem item,
        VoorheesNode under)
    {
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment line: always fine. */
            return(null);
        }

        if (item.Vi_node.Vn_IsContainer())
        {   /* A header: only an include needs checking. */
            if (item.Vi_node.Vn_iKind == VCF_INCLUDE && item.Vi_sKey.Length == 0)
            {   /* [include ] with nothing after it. */
                return("an include needs a file name, as in [include macros.cfg]");
            }
            return(null);
        }

        if (item.Vi_sKeyGap == null)
        {   /* No delimiter: not an option. */
            return("this line is not a section, an option or a comment "
                + "(an option needs \"=\" or \":\" after its name)");
        }

        if (item.Vi_sKey.Length == 0)
        {   /* Nothing before the delimiter. */
            return("an option needs a name before its \"=\" or \":\"");
        }

        if (under == null || under.Vn_iKind == VCF_AUTOSAVE)
        {   /* Before the first section (of the file, or of the block). */
            return("an option must be inside a section: put a [section] line "
                + "above it");
        }

        if (under.Vn_iKind == VCF_INCLUDE
            && iFormat != VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* Directly under an include. */
            return(Vcf_ProgramName(iFormat) + " reads no options after an "
                + "[include ...] line: put them under a [section] line");
        }

        /* A valid option. */
        return(null);
    }

    /*-------------------------------------------------------------------------*/
    /* Vcf_ParseBlock:                                                         */
    /*                                                                         */
    /* An auto-saved block starting at a line, if a clean one does (decision   */
    /* K2).  Klipper: the three header lines exactly (configfile.py            */
    /* AUTOSAVE_HEADER), a line break after them, no line starting "#*# "      */
    /* before them and no second header after them; then every line through    */
    /* the last non-blank one is "#*#" (an empty line) or "#*# " + text.       */
    /* KlipperScreen: its marker line (and the "#~#" line it writes after      */
    /* it, which joins the header); then every line through the last           */
    /* non-blank one is "#~# " + text, except the "#~#" lines it writes at     */
    /* the end.  The lines with the prefix removed are read like a file        */
    /* (sections, options, comments); if that fails, or any line breaks the    */
    /* rules (a line "#*# " with nothing after it would not write back the     */
    /* same), there is no block: the lines stay comments and opening the       */
    /* file says the block is damaged.                                         */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     iFormat    : the dialect (only Klipper and KlipperScreen have one). */
    /*     sText      : the whole text.                                        */
    /*     iLineStart : where the line starts (not indented).                  */
    /*     iBlockEnd  : set to where the block's last non-blank line ends.     */
    /*                                                                         */
    /* Returns:                                                                */
    /*     VoorheesItem : the block item (lead not set), or null for none.     */
    /*-------------------------------------------------------------------------*/
    public static VoorheesItem Vcf_ParseBlock(int iFormat, string sText,
        int iLineStart, out int iBlockEnd)
    {
        List<int> lstStarts;                             // each line after the header: its start
        List<int> lstEnds;                               // and its content end
        StringBuilder sbInner;                           // the lines with the prefix removed
        VoorheesNode innerRoot;                          // those lines, read
        VoorheesNode node;                               // the block's container
        VoorheesItem item;                               // the block
        string sFirstLine;                               // the header's first line
        string sKey;                                     // the block's name
        string sLine;                                    // a line looked at
        string sStripped;                                // a block line without its prefix
        string sError;                                   // why the inner lines do not read
        int iHeaderEnd;                                  // where the header's last line ends
        int iEnd;                                        // a line's content end
        int iNext;                                       // the next line's start
        int iLast;                                       // the last non-blank line after the header
        int iCoreCount;                                  // the lines read as the block's contents
        int iPrevEnd;                                    // end of the previous line put in
        int iCoreEnd;                                    // end of the last of them
        int i;

        iBlockEnd = -1;
        iEnd = Vcf_ContentEnd(sText, iLineStart);
        sLine = sText.Substring(iLineStart, iEnd - iLineStart);
        if (iFormat == VoorheesFormat.VF_KLIPPER)
        {   /* Klipper: the three header lines, then a line break. */
            if (sLine != VCF_SAVECONFIGLINE1)
            {   /* Not the header. */
                return(null);
            }
            iNext = Vcf_NextLine(sText, iEnd);
            if (iNext < 0 || Vcf_LineAt(sText, iNext) != VCF_SAVECONFIGLINE2)
            {   /* Not its second line. */
                return(null);
            }
            iNext = Vcf_NextLine(sText, Vcf_ContentEnd(sText, iNext));
            if (iNext < 0 || Vcf_LineAt(sText, iNext) != VCF_SAVECONFIGLINE3)
            {   /* Not its third line. */
                return(null);
            }
            iHeaderEnd = Vcf_ContentEnd(sText, iNext);
            if (Vcf_NextLine(sText, iHeaderEnd) < 0)
            {   /* No line break after it: Klipper would not find the header. */
                return(null);
            }

            if (sText.Substring(0, iLineStart).IndexOf("\n" + VCF_SAVECONFIGPREFIX
                + " ", StringComparison.Ordinal) >= 0)
            {   /* A "#*# " line above the block: Klipper calls the block corrupted. */
                return(null);
            }
            sFirstLine = VCF_SAVECONFIGLINE1;
            sKey = VCF_SAVECONFIGKEY;
        }
        else if (iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* KlipperScreen: its marker line, and the "#~#" line after it if there is one. */
            if (sLine != VCF_KSMARKER)
            {   /* Not the marker. */
                return(null);
            }
            iHeaderEnd = iEnd;
            iNext = Vcf_NextLine(sText, iEnd);
            if (iNext >= 0 && Vcf_LineAt(sText, iNext) == VCF_KSPREFIX)
            {   /* The "#~#" line KlipperScreen writes after the marker. */
                iHeaderEnd = Vcf_ContentEnd(sText, iNext);
            }
            sFirstLine = VCF_KSMARKER;
            sKey = VCF_KSBLOCKKEY;
        }
        else
        {   /* Moonraker has no auto-saved block. */
            return(null);
        }

        if (sText.IndexOf(sFirstLine, iHeaderEnd, StringComparison.Ordinal) >= 0)
        {   /* A second header further on: corrupted (Klipper), or ambiguous. */
            return(null);
        }

        /* The lines after the header, and the last one that is not blank. */
        lstStarts = new List<int>();
        lstEnds = new List<int>();
        iLast = -1;
        iNext = Vcf_NextLine(sText, iHeaderEnd);
        while (iNext >= 0)
        {
            iEnd = Vcf_ContentEnd(sText, iNext);
            lstStarts.Add(iNext);
            lstEnds.Add(iEnd);
            if (!Vcf_IsBlank(sText.Substring(iNext, iEnd - iNext)))
            {   /* Not blank: the block runs at least to here. */
                iLast = lstStarts.Count - 1;
            }
            iNext = Vcf_NextLine(sText, iEnd);
        }

        /* The contents: through the last non-blank line, less the "#~#"   */
        /* lines KlipperScreen writes at the end of its block.             */
        iCoreCount = iLast + 1;
        while (iFormat == VoorheesFormat.VF_KLIPPERSCREEN && iCoreCount > 0
            && Vcf_LineAt(sText, lstStarts[iCoreCount - 1]) == VCF_KSPREFIX)
        {
            iCoreCount--;
        }

        /* Each content line without its prefix, with the line breaks between. */
        sbInner = new StringBuilder();
        iPrevEnd = iHeaderEnd;
        for (i = 0; i < iCoreCount; i++)
        {
            sLine = sText.Substring(lstStarts[i], lstEnds[i] - lstStarts[i]);
            if (!Vcf_StripLine(iFormat, sLine, out sStripped))
            {   /* A line the program would not read, or that would not write back the same. */
                return(null);
            }
            sbInner.Append(sText, iPrevEnd, lstStarts[i] - iPrevEnd);
            sbInner.Append(sStripped);
            iPrevEnd = lstEnds[i];
        }
        iCoreEnd = iPrevEnd;
        if (iLast >= 0)
        {   /* The block ends with its last non-blank line. */
            iBlockEnd = lstEnds[iLast];
        }
        else
        {   /* Nothing after the header but blank lines. */
            iBlockEnd = iHeaderEnd;
        }

        /* The contents read like a file (no block inside a block). */
        innerRoot = VoorheesLines.Vln_ParseLines(iFormat, sbInner.ToString(), null,
            false, null, out sError);
        if (innerRoot == null)
        {   /* Not valid: the program would fail on it too. */
            iBlockEnd = -1;
            return(null);
        }

        node = VoorheesNode.Vn_NewContainer(VCF_AUTOSAVE);
        node.Vn_lstItems.AddRange(innerRoot.Vn_lstItems);
        if (innerRoot.Vn_sClose == null)
        {   /* No contents at all: only the trailing lines close it. */
            node.Vn_sClose = sText.Substring(iCoreEnd, iBlockEnd - iCoreEnd);
        }
        else
        {   /* The contents' closing text with its prefixes, then the trailing lines. */
            node.Vn_sClose = Vcf_PrefixText(iFormat, innerRoot.Vn_sClose)
                + sText.Substring(iCoreEnd, iBlockEnd - iCoreEnd);
        }

        item = VoorheesItem.Vi_NewMember(sKey, node);
        item.Vi_sKeyRaw = sText.Substring(iLineStart, iHeaderEnd - iLineStart);

        /* The block, read. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_StripLine:                                                         */
    /*                                                                        */
    /* A block line without its prefix, if it is a clean one: Klipper "#*#"   */
    /* (an empty line) or "#*# " + at least one character; KlipperScreen      */
    /* "#~# " + anything.  Anything else would not be read by the program, or */
    /* would not be written back the same.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat   : the dialect.                                           */
    /*     sLine     : the line.                                              */
    /*     sStripped : set to the line without its prefix, or null.           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for a clean line.                                      */
    /*------------------------------------------------------------------------*/
    static bool Vcf_StripLine(int iFormat, string sLine, out string sStripped)
    {
        sStripped = null;
        if (iFormat == VoorheesFormat.VF_KLIPPER)
        {   /* Klipper: "#*#" or "#*# text". */
            if (sLine == VCF_SAVECONFIGPREFIX)
            {   /* An empty line. */
                sStripped = "";
                return(true);
            }
            if (sLine.Length > VCF_SAVECONFIGPREFIX.Length + 1
                && sLine.StartsWith(VCF_SAVECONFIGPREFIX + " ", StringComparison.Ordinal))
            {   /* Prefix, space, text. */
                sStripped = sLine.Substring(VCF_SAVECONFIGPREFIX.Length + 1);
                return(true);
            }
            return(false);
        }

        if (sLine.StartsWith(VCF_KSPREFIX + " ", StringComparison.Ordinal))
        {   /* KlipperScreen: prefix, space, text (possibly none). */
            sStripped = sLine.Substring(VCF_KSPREFIX.Length + 1);
            return(true);
        }

        /* Not a clean block line. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_PrefixLine:                                                        */
    /*                                                                        */
    /* A block's content line with its prefix: Klipper "#*#" for an empty     */
    /* line, else "#*# " + the line (what Klipper's own                       */
    /* ('#*# ' + l).strip() gives); KlipperScreen "#~# " + the line.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     sLine   : the line without its prefix.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the line as the file holds it.                            */
    /*------------------------------------------------------------------------*/
    static string Vcf_PrefixLine(int iFormat, string sLine)
    {
        if (iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* KlipperScreen: always prefix and space. */
            return(VCF_KSPREFIX + " " + sLine);
        }

        if (sLine.Length == 0)
        {   /* Klipper: an empty line is the prefix alone. */
            return(VCF_SAVECONFIGPREFIX);
        }

        /* Klipper: prefix, space, text. */
        return(VCF_SAVECONFIGPREFIX + " " + sLine);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_PrefixText:                                                        */
    /*                                                                        */
    /* Block contents with every line prefixed (Vcf_PrefixLine), line breaks  */
    /* kept.  The text before the first line break continues the line it      */
    /* follows (the header's last line, or a line already written), so it     */
    /* gets no prefix; every line after a line break is a whole line.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     sText   : the contents without prefixes.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the contents as the file holds them.                      */
    /*------------------------------------------------------------------------*/
    static string Vcf_PrefixText(int iFormat, string sText)
    {
        StringBuilder sb;                                // the prefixed text
        bool bFirst;                                     // still before the first line break
        int iStart;                                      // where the current line starts
        int iBreak;                                      // its line break
        int iAfter;                                      // where the next line starts

        sb = new StringBuilder();
        bFirst = true;
        iStart = 0;
        while (true)
        {
            iBreak = sText.IndexOfAny(new char[] { '\r', '\n' }, iStart);
            if (iBreak < 0)
            {   /* The last line: no break after it. */
                break;
            }

            /* The line (prefixed unless it continues an earlier one), then its break. */
            if (bFirst)
            {   /* Continues the line before. */
                sb.Append(sText, iStart, iBreak - iStart);
            }
            else
            {   /* A whole line. */
                sb.Append(Vcf_PrefixLine(iFormat, sText.Substring(iStart, iBreak - iStart)));
            }
            iAfter = iBreak + 1;
            if (sText[iBreak] == '\r' && iAfter < sText.Length && sText[iAfter] == '\n')
            {   /* CRLF. */
                iAfter++;
            }
            sb.Append(sText, iBreak, iAfter - iBreak);
            iStart = iAfter;
            bFirst = false;
        }

        if (bFirst)
        {   /* No line break at all: it all continues the line before. */
            sb.Append(sText, iStart, sText.Length - iStart);
        }
        else
        {   /* The last whole line. */
            sb.Append(Vcf_PrefixLine(iFormat, sText.Substring(iStart)));
        }

        /* The text with its prefixes. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_ContentEnd:                                                        */
    /*                                                                        */
    /* Where a line's content ends: its first CR or LF, or the end.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText  : the text.                                                 */
    /*     iStart : where the line starts.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the position of its line break, or sText.Length.             */
    /*------------------------------------------------------------------------*/
    static int Vcf_ContentEnd(string sText, int iStart)
    {
        int i;

        i = iStart;
        while (i < sText.Length && sText[i] != '\r' && sText[i] != '\n')
        {
            i++;
        }

        /* The line break, or the end. */
        return(i);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_NextLine:                                                          */
    /*                                                                        */
    /* Where the line after a line's content starts: past its CRLF, LF or     */
    /* lone CR.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText       : the text.                                            */
    /*     iContentEnd : where the line's content ends.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the next line's start (sText.Length after a final break), or */
    /*           -1 when the line has no line break.                          */
    /*------------------------------------------------------------------------*/
    static int Vcf_NextLine(string sText, int iContentEnd)
    {
        if (iContentEnd >= sText.Length)
        {   /* The last line, with no break. */
            return(-1);
        }

        if (sText[iContentEnd] == '\r' && iContentEnd + 1 < sText.Length
            && sText[iContentEnd + 1] == '\n')
        {   /* CRLF. */
            return(iContentEnd + 2);
        }

        /* LF or a lone CR. */
        return(iContentEnd + 1);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_LineAt:                                                            */
    /*                                                                        */
    /* A line's content.                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText  : the text.                                                 */
    /*     iStart : where the line starts.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the line without its line break.                          */
    /*------------------------------------------------------------------------*/
    static string Vcf_LineAt(string sText, int iStart)
    {
        /* Up to its line break. */
        return(sText.Substring(iStart, Vcf_ContentEnd(sText, iStart) - iStart));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_WriteEntry:                                                        */
    /*                                                                        */
    /* An option's line(s): the name, the gap with its delimiter (generated:  */
    /* the file's usual one, without trailing spaces when the value's first   */
    /* line is empty), the value (as written, or generated from its text:     */
    /* Vcf_GenerateValue), with the first line's trailing spaces and inline   */
    /* comment put back at the end of the FIRST line.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc   : the document (its style).                                  */
    /*     item  : the option.                                                */
    /*     sLead : the lead written before it (its indentation sets a         */
    /*             generated value's continuation indent).                    */
    /*     sb    : where the text goes.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the text is appended (no line break after it).              */
    /*------------------------------------------------------------------------*/
    public static void Vcf_WriteEntry(VoorheesDocument doc, VoorheesItem item,
        string sLead, StringBuilder sb)
    {
        string sGap;                                     // name to value
        string sValue;                                   // the value as written
        int iBreak;                                      // the value's first line break

        if (item.Vi_sKeyGap != null)
        {   /* As in the file. */
            sGap = item.Vi_sKeyGap;
        }
        else if (item.Vi_node.Vn_sRaw == null
            && Vcf_FirstLine(item.Vi_node.Vn_sText).Length == 0)
        {   /* New, with nothing on the first line: "name:" without a trailing space. */
            sGap = doc.Vd_sDelimiter.TrimEnd(' ', '\t');
        }
        else
        {   /* New: the file's usual delimiter. */
            sGap = doc.Vd_sDelimiter;
        }

        if (item.Vi_node.Vn_sRaw != null)
        {   /* As written. */
            sValue = item.Vi_node.Vn_sRaw;
        }
        else
        {   /* Generated from the text. */
            sValue = Vcf_GenerateValue(doc, item.Vi_node.Vn_sText,
                sGap.Length > 0 && Vcf_IsSpace(sGap[sGap.Length - 1]), sLead);
        }

        sb.Append(item.Vi_sKey);
        sb.Append(sGap);
        iBreak = sValue.IndexOfAny(new char[] { '\r', '\n' });
        if (iBreak < 0)
        {   /* One line: value, trailing spaces, comment. */
            sb.Append(sValue);
            Vcf_WriteLineEnd(item, sb);
        }
        else
        {   /* Several: the first line, its end, then the continuation lines. */
            sb.Append(sValue, 0, iBreak);
            Vcf_WriteLineEnd(item, sb);
            sb.Append(sValue, iBreak, sValue.Length - iBreak);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_WriteLineEnd:                                                      */
    /*                                                                        */
    /* What follows an option's (first-line) value on its line: the trailing  */
    /* spaces as written, then the inline comment with the spaces before it   */
    /* (one space when generated).                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the option.                                                 */
    /*     sb   : where the text goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the text is appended.                                       */
    /*------------------------------------------------------------------------*/
    static void Vcf_WriteLineEnd(VoorheesItem item, StringBuilder sb)
    {
        if (item.Vi_sValueGap != null)
        {   /* Trailing spaces as written. */
            sb.Append(item.Vi_sValueGap);
        }

        if (item.Vi_sEolComment != null)
        {   /* An inline comment, after its spaces. */
            if (item.Vi_sEolGap != null)
            {   /* As written. */
                sb.Append(item.Vi_sEolGap);
            }
            else
            {   /* New: one space (Moonraker needs whitespace before it). */
                sb.Append(' ');
            }
            sb.Append(item.Vi_sEolComment);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_FirstLine:                                                         */
    /*                                                                        */
    /* A text's first line.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : up to its first line break.                               */
    /*------------------------------------------------------------------------*/
    static string Vcf_FirstLine(string sText)
    {
        int iBreak;                                      // the first line break

        iBreak = sText.IndexOfAny(new char[] { '\r', '\n' });
        if (iBreak < 0)
        {   /* One line. */
            return(sText);
        }

        /* Up to the break. */
        return(sText.Substring(0, iBreak));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_GenerateValue:                                                     */
    /*                                                                        */
    /* A value as it is to be written, from its text: the first line          */
    /* (Moonraker escapes encoded), then each further line on a line of its   */
    /* own, indented by the continuation indent (an empty line stays empty).  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc         : the document (line ending, continuation indent).     */
    /*     sText       : the value's text (LF line breaks).                   */
    /*     bAfterSpace : the gap before the value ends in a space or tab.     */
    /*     sLead       : the option's lead (for its own indentation).         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the value as written.                                     */
    /*------------------------------------------------------------------------*/
    static string Vcf_GenerateValue(VoorheesDocument doc, string sText,
        bool bAfterSpace, string sLead)
    {
        string[] arrLines;                               // the value's lines
        StringBuilder sb;                                // the value as written
        string sIndent;                                  // continuation indent
        int i;

        arrLines = sText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        sb = new StringBuilder();
        sb.Append(Vcf_EncodeLine(doc.Vd_iFormat, arrLines[0], bAfterSpace));
        if (arrLines.Length == 1)
        {   /* One line. */
            return(sb.ToString());
        }

        sIndent = Vcf_OwnIndent(sLead) + doc.Vd_sIndentUnit;
        for (i = 1; i < arrLines.Length; i++)
        {
            sb.Append(doc.Vd_sNewline);
            if (arrLines[i].Length > 0)
            {   /* A line with something on it: indented deeper than the option. */
                sb.Append(sIndent);
                sb.Append(arrLines[i]);
            }
        }

        /* Every line. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_OwnIndent:                                                         */
    /*                                                                        */
    /* An item's own indentation: what follows the last line break in its     */
    /* lead.                                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sLead : the lead, or null.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the indentation ("" for none).                            */
    /*------------------------------------------------------------------------*/
    static string Vcf_OwnIndent(string sLead)
    {
        int iBreak;                                      // the lead's last line break

        if (sLead == null)
        {   /* No lead known: no indentation. */
            return("");
        }

        iBreak = sLead.LastIndexOfAny(new char[] { '\r', '\n' });

        /* After the last break (or all of it at the start of the file). */
        return(sLead.Substring(iBreak + 1));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_WriteHeader:                                                       */
    /*                                                                        */
    /* A section's or an include's header line: "[" + the name as written     */
    /* (generated: the name, or "include " + the path) + "]", then its        */
    /* comment (with the spaces before it, one space when generated) or the   */
    /* other text that followed it.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the section or include.                                     */
    /*     sb   : where the line goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended.                                       */
    /*------------------------------------------------------------------------*/
    public static void Vcf_WriteHeader(VoorheesItem item, StringBuilder sb)
    {
        sb.Append('[');
        if (item.Vi_sKeyRaw != null)
        {   /* As written. */
            sb.Append(item.Vi_sKeyRaw);
        }
        else if (item.Vi_node.Vn_iKind == VCF_INCLUDE)
        {   /* An include: the word, then the path. */
            sb.Append(VCF_INCLUDEWORD);
            sb.Append(item.Vi_sKey);
        }
        else
        {   /* A section: its name. */
            sb.Append(item.Vi_sKey);
        }
        sb.Append(']');

        if (item.Vi_sOpenComment != null)
        {   /* A comment on the header line. */
            if (item.Vi_sOpenGap != null)
            {   /* Its spacing as written. */
                sb.Append(item.Vi_sOpenGap);
            }
            else
            {   /* New: one space. */
                sb.Append(' ');
            }
            sb.Append(item.Vi_sOpenComment);
        }
        else if (item.Vi_sValueGap != null)
        {   /* Other text as written. */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_WriteBlock:                                                        */
    /*                                                                        */
    /* An auto-saved block: its header lines as written (generated: the       */
    /* program's own), its sections written like a file into a buffer and     */
    /* then prefixed line by line (Vcf_PrefixText), then its closing text.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc    : the document.                                             */
    /*     item   : the block.                                                */
    /*     sb     : where the text goes.                                      */
    /*     iLimit : stop once the contents are longer than this.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it stopped at the limit.                          */
    /*------------------------------------------------------------------------*/
    public static bool Vcf_WriteBlock(VoorheesDocument doc, VoorheesItem item,
        StringBuilder sb, int iLimit)
    {
        StringBuilder sbInner;                           // the contents without prefixes
        bool bStopped;                                   // the contents stopped at the limit
        string sDelimiter;                               // the file's delimiter, put back after
        string sIndent;                                  // the file's continuation indent, likewise

        if (item.Vi_sKeyRaw != null)
        {   /* The header as written. */
            sb.Append(item.Vi_sKeyRaw);
        }
        else if (doc.Vd_iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* KlipperScreen's marker and the "#~#" after it. */
            sb.Append(VCF_KSMARKER);
            sb.Append(doc.Vd_sNewline);
            sb.Append(VCF_KSPREFIX);
        }
        else
        {   /* Klipper's three header lines. */
            sb.Append(VCF_SAVECONFIGLINE1);
            sb.Append(doc.Vd_sNewline);
            sb.Append(VCF_SAVECONFIGLINE2);
            sb.Append(doc.Vd_sNewline);
            sb.Append(VCF_SAVECONFIGLINE3);
        }

        /* The contents, in configparser's own style for anything generated   */
        /* (both programs write the block with configparser.write: "name =    */
        /* value", continuation lines after a tab), not the file's: the       */
        /* document's style fields are swapped for the write and restored.    */
        sbInner = new StringBuilder();
        sDelimiter = doc.Vd_sDelimiter;
        sIndent = doc.Vd_sIndentUnit;
        try
        {
            doc.Vd_sDelimiter = VCF_BLOCKDELIMITER;
            doc.Vd_sIndentUnit = VCF_BLOCKINDENT;
            bStopped = VoorheesLines.Vln_WriteItems(doc, item.Vi_node, sbInner,
                iLimit);
        }
        finally
        {   /* The file's own style again, whatever happened. */
            doc.Vd_sDelimiter = sDelimiter;
            doc.Vd_sIndentUnit = sIndent;
        }
        sb.Append(Vcf_PrefixText(doc.Vd_iFormat, sbInner.ToString()));
        if (bStopped)
        {   /* Cut short. */
            return(true);
        }

        if (item.Vi_node.Vn_sClose != null)
        {   /* The closing text as written. */
            sb.Append(item.Vi_node.Vn_sClose);
        }

        /* Written in full. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_LoadDocument:                                                      */
    /*                                                                        */
    /* A whole file's bytes to a document of a dialect: decoded (any encoding */
    /* Vtx_Decode knows), read by the line engine with this module's rules    */
    /* (refused with line and column where the program refuses it), its       */
    /* style detected: line ending (LF when there is none), the usual         */
    /* delimiter, the usual comment marker, the continuation indent.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     bytes   : the file's contents.                                     */
    /*     sPath   : the file, for the document to remember; may be null.     */
    /*     sError  : set to why the file is refused, else null.               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document, or null when refused.             */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vcf_LoadDocument(int iFormat, byte[] bytes,
        string sPath, out string sError)
    {
        Encoding encoding;                               // the file's encoding
        bool bBom;                                       // it had a byte order mark
        string sText;                                    // the decoded file
        string sNewline;                                 // its line ending
        VoorheesNode root;                               // the document node
        VoorheesNode styled;                             // its items less the auto-saved block, for style detection
        VoorheesDocument doc;                            // the document
        int i;

        sText = VoorheesText.Vtx_Decode(bytes, out encoding, out bBom);
        root = VoorheesLines.Vln_Parse(iFormat, sText, null, out sError);
        if (root == null)
        {   /* Refused (sError has line and column). */
            return(null);
        }

        sNewline = "\n";
        if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
        {   /* Line breaks to go by. */
            sNewline = VoorheesText.Vtx_DetectNewline(sText);
        }

        /* The style is the user's part of the file: the auto-saved block is   */
        /* in configparser's own style (Vcf_WriteBlock keeps it so).           */
        styled = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);
        for (i = 0; i < root.Vn_Count(); i++)
        {
            if (!(root.Vn_lstItems[i].Vi_IsValue()
                && root.Vn_lstItems[i].Vi_node.Vn_iKind == VCF_AUTOSAVE))
            {   /* Not the block: it counts. */
                styled.Vn_lstItems.Add(root.Vn_lstItems[i]);
            }
        }

        doc = VoorheesDocument.Vd_Create(iFormat, root, sPath, encoding, bBom,
            sNewline, Vcf_DetectIndent(styled), null, null);
        doc.Vd_sDelimiter = VoorheesLines.Vln_DetectDelimiter(styled,
            VCF_DEFAULTDELIMITER);
        doc.Vd_sCommentPrefix = VoorheesLines.Vln_DetectCommentPrefix(styled,
            VCF_DEFAULTCOMMENTPREFIX);

        /* The document with its encoding and style. */
        return(doc);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_DetectIndent:                                                      */
    /*                                                                        */
    /* The file's continuation indent: for each multi-line value, the         */
    /* indentation its value lines share beyond the option's own; the most    */
    /* common one wins.                                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root : the document node.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the indent (VCF_DEFAULTINDENT when there are no           */
    /*              multi-line values).                                       */
    /*------------------------------------------------------------------------*/
    static string Vcf_DetectIndent(VoorheesNode root)
    {
        Dictionary<string, int> dictCounts;              // indent to times seen
        List<string> lstOrder;                           // indents in the order first seen

        dictCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        lstOrder = new List<string>();
        Vcf_CountIndents(root, dictCounts, lstOrder);

        /* The most common one, or the default. */
        return(VoorheesLines.Vln_MostCommon(dictCounts, lstOrder,
            VCF_DEFAULTINDENT));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_CountIndents:                                                      */
    /*                                                                        */
    /* Tallies the continuation indents of the multi-line values in a         */
    /* container and the containers inside it.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container  : the container.                                        */
    /*     dictCounts : indent to times seen (added to).                      */
    /*     lstOrder   : indents in the order first seen (added to).           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tallies are updated.                                    */
    /*------------------------------------------------------------------------*/
    static void Vcf_CountIndents(VoorheesNode container,
        Dictionary<string, int> dictCounts, List<string> lstOrder)
    {
        VoorheesItem item;                               // an item looked at
        string[] arrLines;                               // a value's lines
        string sOwn;                                     // the option's own indentation
        string sCommon;                                  // its value lines' shared indentation
        string sIndent;                                  // a line's indentation
        int i;
        int j;

        for (i = 0; i < container.Vn_Count(); i++)
        {
            item = container.Vn_lstItems[i];
            if (item.Vi_iKind != VoorheesItem.VI_MEMBER)
            {   /* A comment. */
                continue;
            }

            if (item.Vi_node.Vn_IsContainer())
            {   /* A section, include or block: its options. */
                Vcf_CountIndents(item.Vi_node, dictCounts, lstOrder);
                continue;
            }

            if (item.Vi_node.Vn_sRaw == null || item.Vi_node.Vn_sRaw.IndexOfAny(
                new char[] { '\r', '\n' }) < 0)
            {   /* One line: no continuation indent. */
                continue;
            }

            /* The indentation the value lines share. */
            arrLines = item.Vi_node.Vn_sRaw.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n');
            sCommon = null;
            for (j = 1; j < arrLines.Length; j++)
            {
                if (Vcf_IsBlank(arrLines[j]) || Vcf_IsCommentLine(arrLines[j]))
                {   /* Does not count. */
                    continue;
                }
                sIndent = arrLines[j].Substring(0, arrLines[j].Length
                    - arrLines[j].TrimStart(' ', '\t').Length);
                if (sCommon == null || sIndent.Length < sCommon.Length)
                {   /* The smallest so far. */
                    sCommon = sIndent;
                }
            }

            sOwn = Vcf_OwnIndent(item.Vi_sLead);
            if (sCommon != null && sCommon.Length > sOwn.Length)
            {   /* Beyond the option's own: that is the continuation indent. */
                VoorheesLines.Vln_Tally(dictCounts, lstOrder,
                    sCommon.Substring(sOwn.Length));
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_SaveBytes:                                                         */
    /*                                                                        */
    /* A document to the bytes of its file, in its own encoding.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the file's new contents.                                  */
    /*------------------------------------------------------------------------*/
    public static byte[] Vcf_SaveBytes(VoorheesDocument doc)
    {
        /* Text by the line engine, bytes as the file had them. */
        return(VoorheesText.Vtx_Encode(VoorheesLines.Vln_Write(doc),
            doc.Vd_encoding, doc.Vd_bBom));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_NewDocument:                                                       */
    /*                                                                        */
    /* A new, empty document of a dialect: UTF-8 without a BOM, LF (the files */
    /* live on Linux), "name: value", "#" comments, two-space continuation    */
    /* lines.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document.                                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vcf_NewDocument(int iFormat)
    {
        VoorheesNode root;                               // the empty document node

        root = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);
        root.Vn_bOneLine = false;

        /* LF and the indent here; delimiter and marker from Vf_ApplyDefaultStyle. */
        return(VoorheesDocument.Vd_Create(iFormat, root, null, null, false,
            "\n", VCF_DEFAULTINDENT, null, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_KindName:                                                          */
    /*                                                                        */
    /* A kind's name, for the Type box, menus and messages.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VCF_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name; "Value" for anything else.                      */
    /*------------------------------------------------------------------------*/
    public static string Vcf_KindName(int iKind)
    {
        switch (iKind)
        {
            case VCF_TEXT:     return("Text");                   // name: value
            case VCF_SECTION:  return("Section");                // [name]
            case VCF_INCLUDE:  return("Include");                // [include path]
            case VCF_AUTOSAVE: return("Auto-saved block");       // #*# / #~# block
            default:           return("Value");                  // not a CFG kind
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_ChildKinds:                                                        */
    /*                                                                        */
    /* What Add Child offers in a container: sections and includes at the     */
    /* top level; sections in an auto-saved block; options in a section (and  */
    /* in a KlipperScreen include, which reads them); nothing but comments    */
    /* under a Klipper or Moonraker include.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat   : the dialect.                                           */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds, in menu order (a new list).                 */
    /*------------------------------------------------------------------------*/
    public static List<int> Vcf_ChildKinds(int iFormat, VoorheesNode container)
    {
        List<int> lstKinds;                              // the kinds

        lstKinds = new List<int>();
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top level: sections and includes. */
            lstKinds.Add(VCF_SECTION);
            lstKinds.Add(VCF_INCLUDE);
        }
        else if (container.Vn_iKind == VCF_AUTOSAVE)
        {   /* An auto-saved block: sections. */
            lstKinds.Add(VCF_SECTION);
        }
        else if (container.Vn_iKind == VCF_SECTION
            || (container.Vn_iKind == VCF_INCLUDE
            && iFormat == VoorheesFormat.VF_KLIPPERSCREEN))
        {   /* A section: options. */
            lstKinds.Add(VCF_TEXT);
        }

        /* The kinds that fit (an empty list: comments only). */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_PanelKinds:                                                        */
    /*                                                                        */
    /* What the Type box offers for an entry: a container stays what it is    */
    /* (its lines would have nowhere to go); an option is Text.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the entry's value.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds (a new list).                                */
    /*------------------------------------------------------------------------*/
    public static List<int> Vcf_PanelKinds(VoorheesNode node)
    {
        List<int> lstKinds;                              // the kinds

        lstKinds = new List<int>();
        if (node.Vn_IsContainer())
        {   /* A section, include or block: only that. */
            lstKinds.Add(node.Vn_iKind);
        }
        else
        {   /* An option: text. */
            lstKinds.Add(VCF_TEXT);
        }

        /* The one kind it can be. */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_MarkerProblem:                                                     */
    /*                                                                        */
    /* Why a piece of a line (a name, a one-line value) would be cut by the   */
    /* dialect's inline comments, if it would: Klipper reads nothing after a  */
    /* "#", and Klipper and Moonraker read "#" or ";" after a space or tab as */
    /* a comment.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     s       : the text.                                                */
    /*     sWhat   : what it is, for the message ("A value", "A name").       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null.                                      */
    /*------------------------------------------------------------------------*/
    static string Vcf_MarkerProblem(int iFormat, string s, string sWhat)
    {
        int i;

        if (iFormat == VoorheesFormat.VF_KLIPPER && s.IndexOf('#') >= 0)
        {   /* Klipper cuts every line at "#". */
            return(sWhat + " can't contain \"#\": Klipper ignores everything "
                + "after it on a line.");
        }

        if (iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* No inline comments. */
            return(null);
        }

        for (i = 1; i < s.Length; i++)
        {
            if ((s[i] == '#' || s[i] == ';') && Vcf_IsSpace(s[i - 1]))
            {   /* A marker after whitespace. */
                return(sWhat + " can't contain \"" + s[i].ToString()
                    + "\" after a space: " + Vcf_ProgramName(iFormat)
                    + " reads the rest of the line as a comment.");
            }
        }

        /* Nothing would be cut. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_MakeValue:                                                         */
    /*                                                                        */
    /* An option's value from text (the Value box, in-place editing,          */
    /* Replace), or why the text can't be one:                                */
    /*     o the first line is a one-line value: no spaces at its ends (the   */
    /*       programs strip them); in Klipper no "#" and no ";" after a       */
    /*       space (they would start a comment; Moonraker escapes them        */
    /*       instead, so they are fine there);                                */
    /*     o further lines make a multi-line value, written as source: they   */
    /*       may hold anything, comments included, but the last one must be   */
    /*       part of the value (not empty, not a comment), or the value would */
    /*       end before it when read back.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     sText   : the text (any line breaks).                              */
    /*     sError  : set to why it is refused, else null.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value (LF line breaks, to be generated), or     */
    /*                    null when refused.                                  */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vcf_MakeValue(int iFormat, string sText,
        out string sError)
    {
        string sValue;                                   // the text with LF line breaks
        string[] arrLines;                               // its lines
        string sFirst;                                   // the first line
        string sLast;                                    // the last line

        sError = null;
        sValue = sText.Replace("\r\n", "\n").Replace('\r', '\n');
        arrLines = sValue.Split('\n');
        sFirst = arrLines[0];

        if (sFirst.Trim(' ', '\t').Length != sFirst.Length)
        {   /* Spaces at an end of the first line. */
            sError = Vcf_ProgramName(iFormat) + " strips spaces at the start "
                + "and end of a value.";
            return(null);
        }

        if (iFormat == VoorheesFormat.VF_KLIPPER)
        {   /* Klipper: no comment markers in the first line. */
            sError = Vcf_MarkerProblem(iFormat, sFirst, "A value");
            if (sError != null)
            {   /* It would be cut. */
                return(null);
            }
        }

        if (arrLines.Length > 1)
        {   /* Multi-line: the last line must belong to the value. */
            sLast = arrLines[arrLines.Length - 1];
            if (Vcf_IsBlank(sLast) || Vcf_IsCommentLine(sLast))
            {   /* It would read back as outside the value. */
                sError = "The last line of a multi-line value must be part of "
                    + "the value (not empty, and not a comment).";
                return(null);
            }
        }

        /* The value, to be written in the file's style. */
        return(VoorheesNode.Vn_NewLeaf(VCF_TEXT, sValue));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_ValueFromPanel:                                                    */
    /*                                                                        */
    /* The value the edit panel describes: Text from the Value box            */
    /* (Vcf_MakeValue; unchanged text keeps the value exactly as written), or */
    /* a section, include or block staying itself.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     iKind   : the VCF_ kind chosen.                                    */
    /*     sText   : the Value box's text.                                    */
    /*     oldNode : the value being replaced.                                */
    /*     sError  : set to why the value is refused, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null when refused.                    */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vcf_ValueFromPanel(int iFormat, int iKind,
        string sText, VoorheesNode oldNode, out string sError)
    {
        sError = null;
        if (iKind != VCF_TEXT)
        {   /* A container stays itself (the Type box offers nothing else for it). */
            if (oldNode.Vn_iKind == iKind)
            {   /* Unchanged, lines and all. */
                return(oldNode);
            }
            sError = "An option can't become a " + Vcf_KindName(iKind).ToLowerInvariant()
                + "; add one at the top level instead.";
            return(null);
        }

        if (oldNode.Vn_IsContainer())
        {   /* A container can't become an option. */
            sError = "A " + Vcf_KindName(oldNode.Vn_iKind).ToLowerInvariant()
                + " can't become an option.";
            return(null);
        }

        if (sText.Replace("\r\n", "\n").Replace('\r', '\n') == oldNode.Vn_sText)
        {   /* The same text: the value exactly as it was written. */
            return(oldNode);
        }

        /* New text: a valid value, or refused with the reason. */
        return(Vcf_MakeValue(iFormat, sText, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_NewValue:                                                          */
    /*                                                                        */
    /* An empty value of a kind: "" text, or an empty section, include or     */
    /* block.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VCF_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value.                                          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vcf_NewValue(int iKind)
    {
        VoorheesNode container;                          // a new container

        if (iKind == VCF_SECTION || iKind == VCF_INCLUDE || iKind == VCF_AUTOSAVE)
        {   /* An empty container. */
            container = VoorheesNode.Vn_NewContainer(iKind);
            container.Vn_sClose = "";
            return(container);
        }

        /* Empty text. */
        return(VoorheesNode.Vn_NewLeaf(VCF_TEXT, ""));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_ConvertText:                                                       */
    /*                                                                        */
    /* A value from edited text (in-place editing, Replace): the same text    */
    /* keeps the value as written; other text must be a valid value           */
    /* (Vcf_MakeValue); a container has no text.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     oldNode : the value being replaced.                                */
    /*     sText   : the new text.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value, or null when refused.                */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vcf_ConvertText(int iFormat, VoorheesNode oldNode,
        string sText)
    {
        string sError;                                   // why refused (not reported here)

        if (oldNode.Vn_IsContainer())
        {   /* No text to change. */
            return(null);
        }

        if (sText.Replace("\r\n", "\n").Replace('\r', '\n') == oldNode.Vn_sText)
        {   /* Unchanged. */
            return(oldNode);
        }

        /* A valid value, or null. */
        return(Vcf_MakeValue(iFormat, sText, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_NewChild:                                                          */
    /*                                                                        */
    /* Add Child's new item: a section, include or option named with the      */
    /* first free variant of its base name ("new_section", "new_file.cfg" or  */
    /* ".conf", "new_option"), holding the kind's empty value.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (for free names).                         */
    /*     container : the container.                                         */
    /*     iKind     : a VCF_ kind.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vcf_NewChild(VoorheesDocument doc,
        VoorheesNode container, int iKind)
    {
        string sBase;                                    // the name to start from

        if (iKind == VCF_SECTION)
        {   /* A section. */
            sBase = VCF_NEWSECTIONBASE;
        }
        else if (iKind == VCF_INCLUDE && doc.Vd_iFormat == VoorheesFormat.VF_KLIPPER)
        {   /* A Klipper include: a .cfg file. */
            sBase = VCF_NEWINCLUDECFG;
        }
        else if (iKind == VCF_INCLUDE)
        {   /* A Moonraker or KlipperScreen include: a .conf file. */
            sBase = VCF_NEWINCLUDECONF;
        }
        else
        {   /* An option. */
            sBase = VCF_NEWOPTIONBASE;
        }

        /* The item with a free name. */
        return(VoorheesItem.Vi_NewMember(doc.Vd_UnusedKey(container, sBase),
            Vcf_NewValue(iKind)));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_NewKeyBase:                                                        */
    /*                                                                        */
    /* The name a new item in a container starts from: "new_section" where    */
    /* sections go (the top level, an auto-saved block), else "new_option".   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the base name.                                            */
    /*------------------------------------------------------------------------*/
    public static string Vcf_NewKeyBase(VoorheesNode container)
    {
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT
            || container.Vn_iKind == VCF_AUTOSAVE)
        {   /* Where sections go. */
            return(VCF_NEWSECTIONBASE);
        }

        /* Inside a section: options. */
        return(VCF_NEWOPTIONBASE);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_FitItem:                                                           */
    /*                                                                        */
    /* Whether an item can go into a container: comments anywhere; sections   */
    /* at the top level or in an auto-saved block; includes at the top level; */
    /* options in a section (or a KlipperScreen include); an auto-saved block */
    /* never (the program writes it; there is one, at the end); every entry   */
    /* needs a name.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat   : the dialect.                                           */
    /*     container : where it goes.                                         */
    /*     item      : the item.                                              */
    /*     sError    : set to why it cannot go there, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item, or null when refused.                     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vcf_FitItem(int iFormat, VoorheesNode container,
        VoorheesItem item, out string sError)
    {
        int iKind;                                       // the item's value kind
        int iWhere;                                      // the container's kind

        sError = null;
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* Comments go anywhere. */
            return(item);
        }

        if (item.Vi_iKind == VoorheesItem.VI_ELEMENT)
        {   /* No name. */
            sError = "Entries in a " + VoorheesFormat.Vf_Name(iFormat)
                + " file need a name.";
            return(null);
        }

        iKind = item.Vi_node.Vn_iKind;
        iWhere = container.Vn_iKind;
        if (iKind == VCF_AUTOSAVE)
        {   /* The block can't be copied or moved. */
            sError = "The auto-saved block can't be copied or added: "
                + Vcf_ProgramName(iFormat) + " writes it itself, once, at the "
                + "end of the file.";
            return(null);
        }

        if (iKind == VCF_SECTION && iWhere != VoorheesNode.VN_DOCUMENT
            && iWhere != VCF_AUTOSAVE)
        {   /* A section inside a section. */
            sError = "A section can only be at the top level (or in the "
                + "auto-saved block).";
            return(null);
        }

        if (iKind == VCF_INCLUDE && iWhere != VoorheesNode.VN_DOCUMENT)
        {   /* An include inside something. */
            sError = "An include can only be at the top level.";
            return(null);
        }

        if (iKind == VCF_TEXT && (iWhere == VoorheesNode.VN_DOCUMENT
            || iWhere == VCF_AUTOSAVE))
        {   /* An option outside any section. */
            sError = "An option must be inside a section.";
            return(null);
        }

        if (iKind == VCF_TEXT && iWhere == VCF_INCLUDE
            && iFormat != VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* An option under an include. */
            sError = Vcf_ProgramName(iFormat) + " reads no options under an "
                + "[include ...] line; put it in a section.";
            return(null);
        }

        /* It fits. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_EndPosition:                                                       */
    /*                                                                        */
    /* Where an item added "at the end" of a container goes: the end, except  */
    /* at the top level of a file with an auto-saved block, where it goes     */
    /* just above the block (which must stay last: everything below its       */
    /* header is the block's).                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the last position an item may take.                          */
    /*------------------------------------------------------------------------*/
    public static int Vcf_EndPosition(VoorheesNode container)
    {
        int iCount;                                      // the container's items

        iCount = container.Vn_Count();
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT && iCount > 0
            && container.Vn_lstItems[iCount - 1].Vi_IsValue()
            && container.Vn_lstItems[iCount - 1].Vi_node.Vn_iKind == VCF_AUTOSAVE)
        {   /* Above the block. */
            return(iCount - 1);
        }

        /* The end. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_KeyProblem:                                                        */
    /*                                                                        */
    /* Why a new name would not read back as itself:                          */
    /*     o any name: one line;                                              */
    /*     o the auto-saved block: its name is fixed;                         */
    /*     o an include's file: not empty, no spaces at its ends (trimmed),   */
    /*       no "]" in Moonraker (its name ends at the first one), no inline  */
    /*       comment markers;                                                 */
    /*     o a section: not empty, not starting "include ", no "]" in         */
    /*       Moonraker, no inline comment markers;                            */
    /*     o an option: not empty, no "=" or ":" (the first ends the name),   */
    /*       not starting with "[", "#" or ";", no spaces at its ends         */
    /*       (stripped), no inline comment markers.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     item    : the entry (its value says what it is).                   */
    /*     sNewKey : the proposed name.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is acceptable.                */
    /*------------------------------------------------------------------------*/
    public static string Vcf_KeyProblem(int iFormat, VoorheesItem item,
        string sNewKey)
    {
        int iKind;                                       // what the entry is

        if (sNewKey.IndexOf('\r') >= 0 || sNewKey.IndexOf('\n') >= 0)
        {   /* A line break. */
            return("A name is one line.");
        }

        iKind = item.Vi_node.Vn_iKind;
        if (iKind == VCF_AUTOSAVE)
        {   /* Fixed. */
            return("The auto-saved block's name can't be changed.");
        }

        if (iKind == VCF_INCLUDE)
        {   /* An include's file. */
            if (sNewKey.Trim(' ', '\t').Length == 0)
            {   /* Nothing. */
                return("An include needs a file name.");
            }
            if (sNewKey.Trim(' ', '\t').Length != sNewKey.Length)
            {   /* Spaces at an end. */
                return(Vcf_ProgramName(iFormat) + " strips spaces at the start "
                    + "and end of an include's file name.");
            }
            if (iFormat == VoorheesFormat.VF_MOONRAKER && sNewKey.IndexOf(']') >= 0)
            {   /* Would end the header early. */
                return("A Moonraker include's file name can't contain \"]\".");
            }
            return(Vcf_MarkerProblem(iFormat, sNewKey, "A file name"));
        }

        if (iKind == VCF_SECTION)
        {   /* A section name. */
            if (sNewKey.Length == 0)
            {   /* Empty. */
                return("A section name can't be empty.");
            }
            if (sNewKey.StartsWith(VCF_INCLUDEWORD, StringComparison.Ordinal))
            {   /* Would read back as an include. */
                return("A section name can't start with \"include \": add an "
                    + "include instead.");
            }
            if (iFormat == VoorheesFormat.VF_MOONRAKER && sNewKey.IndexOf(']') >= 0)
            {   /* Would end the name early. */
                return("A Moonraker section name can't contain \"]\".");
            }
            return(Vcf_MarkerProblem(iFormat, sNewKey, "A section name"));
        }

        /* An option name. */
        if (sNewKey.Length == 0)
        {   /* Empty. */
            return("An option needs a name.");
        }
        if (sNewKey.IndexOf('=') >= 0 || sNewKey.IndexOf(':') >= 0)
        {   /* Would end the name early. */
            return("An option name can't contain \"=\" or \":\".");
        }
        if (sNewKey[0] == '[' || sNewKey[0] == '#' || sNewKey[0] == ';')
        {   /* The line would read as a section or a comment. */
            return("An option name can't start with \"[\", \"#\" or \";\".");
        }
        if (sNewKey.Trim(' ', '\t').Length != sNewKey.Length)
        {   /* Spaces at an end. */
            return(Vcf_ProgramName(iFormat) + " strips spaces at the start and "
                + "end of an option name.");
        }

        /* Acceptable unless a comment marker would cut it. */
        return(Vcf_MarkerProblem(iFormat, sNewKey, "An option name"));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_DuplicateMessage:                                                  */
    /*                                                                        */
    /* Why a name is refused when another entry of the container has it, in   */
    /* the dialect's words (sections compared exactly, options without case). */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the entry that would take the name.                         */
    /*     sKey : the name.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the message.                                              */
    /*------------------------------------------------------------------------*/
    public static string Vcf_DuplicateMessage(VoorheesItem item, string sKey)
    {
        if (item.Vi_IsValue() && item.Vi_node.Vn_iKind == VCF_INCLUDE)
        {   /* An include. */
            return("This file already includes \"" + sKey + "\".");
        }

        if (item.Vi_IsValue() && item.Vi_node.Vn_IsContainer())
        {   /* A section. */
            return("This file already has a section named \"" + sKey + "\".");
        }

        /* An option. */
        return("This section already has an option named \"" + sKey
            + "\" (option names are compared without case).");
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_KeyComparer:                                                       */
    /*                                                                        */
    /* How names in a container compare: section names (the top level, an     */
    /* auto-saved block) exactly, option names without case (configparser     */
    /* lower-cases them).                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     StringComparer : the comparison.                                   */
    /*------------------------------------------------------------------------*/
    public static StringComparer Vcf_KeyComparer(VoorheesNode container)
    {
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT
            || container.Vn_iKind == VCF_AUTOSAVE)
        {   /* Section names: exact. */
            return(StringComparer.Ordinal);
        }

        /* Option names: without case. */
        return(StringComparer.OrdinalIgnoreCase);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_HasLineComment:                                                    */
    /*                                                                        */
    /* Whether an entry can carry a comment on its line: a section's or an    */
    /* include's header line always (the programs ignore what follows the     */
    /* "]"); an option in Klipper and Moonraker (inline comments), not in     */
    /* KlipperScreen (none); the auto-saved block never.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the dialect.                                             */
    /*     item    : the entry.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it has a line comment to show and edit.           */
    /*------------------------------------------------------------------------*/
    public static bool Vcf_HasLineComment(int iFormat, VoorheesItem item)
    {
        if (!item.Vi_IsValue() || item.Vi_node.Vn_iKind == VCF_AUTOSAVE)
        {   /* A comment entry, or the block. */
            return(false);
        }

        if (item.Vi_node.Vn_IsContainer())
        {   /* A header line. */
            return(true);
        }

        /* An option: where the dialect has inline comments. */
        return(iFormat != VoorheesFormat.VF_KLIPPERSCREEN);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_CommentText:                                                       */
    /*                                                                        */
    /* A comment's words: without its "#" or ";" and one space after it.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the comment as stored (or null).                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the words; "" for null.                                   */
    /*------------------------------------------------------------------------*/
    public static string Vcf_CommentText(string sRaw)
    {
        if (sRaw == null)
        {   /* No comment. */
            return("");
        }

        if (sRaw.Length > 0 && (sRaw[0] == '#' || sRaw[0] == ';'))
        {   /* A marker: the words after it (and after one space). */
            if (sRaw.Length > 1 && sRaw[1] == ' ')
            {   /* "# words". */
                return(sRaw.Substring(2));
            }
            return(sRaw.Substring(1));
        }

        /* No marker: as written. */
        return(sRaw);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_MakeComment:                                                       */
    /*                                                                        */
    /* A new comment from its words: the file's usual marker, a space and the */
    /* words (the marker alone for no words).  A comment is one line.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPrefix : the document's comment marker (Vd_sCommentPrefix).       */
    /*     sText   : the words.                                               */
    /*     sError  : set to why they cannot be a comment, else null.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vcf_MakeComment(string sPrefix, string sText,
        out string sError)
    {
        sError = null;
        if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
        {   /* Several lines. */
            sError = "A comment is one line; add another comment entry for "
                + "more.";
            return(null);
        }

        if (sText.Length == 0)
        {   /* No words: the marker alone. */
            return(sPrefix);
        }

        /* Marker, space, words. */
        return(sPrefix + " " + sText);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_RemakeComment:                                                     */
    /*                                                                        */
    /* An existing comment with new words, in its own style: unchanged words  */
    /* give it back as written; otherwise its own marker (and whether a space */
    /* followed it) with the new words; one with no marker gets the file's    */
    /* usual one.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPrefix : the document's comment marker.                           */
    /*     sOldRaw : the comment as stored.                                   */
    /*     sText   : the new words.                                           */
    /*     sError  : set to why they cannot be a comment, else null.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vcf_RemakeComment(string sPrefix, string sOldRaw,
        string sText, out string sError)
    {
        if (sText == Vcf_CommentText(sOldRaw))
        {   /* Same words: exactly as written. */
            sError = null;
            return(sOldRaw);
        }

        if (sOldRaw.Length > 0 && (sOldRaw[0] == '#' || sOldRaw[0] == ';')
            && !(sOldRaw.Length > 1 && sOldRaw[1] == ' ') && sText.Length > 0)
        {   /* Its marker with no space after it: keep that look. */
            if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
            {   /* Several lines. */
                sError = "A comment is one line; add another comment entry "
                    + "for more.";
                return(null);
            }
            sError = null;
            return(sOldRaw.Substring(0, 1) + sText);
        }

        if (sOldRaw.Length > 0 && (sOldRaw[0] == '#' || sOldRaw[0] == ';'))
        {   /* Its own marker, a space, the words. */
            return(Vcf_MakeComment(sOldRaw.Substring(0, 1), sText, out sError));
        }

        /* No marker before: the file's usual one. */
        return(Vcf_MakeComment(sPrefix, sText, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_ResetLayout:                                                       */
    /*                                                                        */
    /* Clears a copied item's layout -- lead, gaps, trailing text, a value's  */
    /* raw form (so it is generated in this file's style: its continuation    */
    /* indent above all) -- and inside a section the same for its lines       */
    /* (Paste).  Names, values and comments stay.  ONLY for a copy not in a   */
    /* document.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the copy.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the layout fields are null.                                 */
    /*------------------------------------------------------------------------*/
    public static void Vcf_ResetLayout(VoorheesItem item)
    {
        int i;

        item.Vi_sLead = null;
        item.Vi_sKeyGap = null;
        item.Vi_sValueGap = null;
        item.Vi_sOpenGap = null;
        item.Vi_sEolGap = null;
        if (item.Vi_node == null)
        {   /* A comment: nothing more. */
            return;
        }

        if (item.Vi_node.Vn_lstItems == null)
        {   /* A value: generated afresh from its text. */
            item.Vi_node.Vn_sRaw = null;
            return;
        }

        /* A container: its lines too. */
        item.Vi_node.Vn_sClose = "";
        for (i = 0; i < item.Vi_node.Vn_lstItems.Count; i++)
        {
            Vcf_ResetLayout(item.Vi_node.Vn_lstItems[i]);
        }
    }

    /*--------------------------------------------------------------------------*/
    /* Vcf_LabelBrackets:                                                       */
    /*                                                                          */
    /* The brackets a container's tree label puts round its name: [section],    */
    /* [include path], and the block shown after its prefix ("#*# SAVE_CONFIG", */
    /* "#~# auto-saved").                                                       */
    /*                                                                          */
    /* Arguments:                                                               */
    /*     iFormat : the dialect.                                               */
    /*     iKind   : the value's kind.                                          */
    /*     sOpen   : set to the text before the name, or null.                  */
    /*     sClose  : set to the text after it, or null.                         */
    /*                                                                          */
    /* Returns:                                                                 */
    /*     bool : true for a container.                                         */
    /*--------------------------------------------------------------------------*/
    public static bool Vcf_LabelBrackets(int iFormat, int iKind,
        out string sOpen, out string sClose)
    {
        sOpen = null;
        sClose = null;
        if (iKind == VCF_SECTION)
        {   /* [name]. */
            sOpen = "[";
            sClose = "]";
        }
        else if (iKind == VCF_INCLUDE)
        {   /* [include path]. */
            sOpen = "[" + VCF_INCLUDEWORD;
            sClose = "]";
        }
        else if (iKind == VCF_AUTOSAVE && iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* #~# auto-saved. */
            sOpen = VCF_KSPREFIX + " ";
            sClose = "";
        }
        else if (iKind == VCF_AUTOSAVE)
        {   /* #*# SAVE_CONFIG. */
            sOpen = VCF_SAVECONFIGPREFIX + " ";
            sClose = "";
        }

        /* Bracketed when a container. */
        return(sOpen != null);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_CountLonePercent:                                                  */
    /*                                                                        */
    /* How many values hold a "%" that KlipperScreen's configparser would     */
    /* read as the start of a substitution: not "%%" and not "%(".            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the document node (or a container in it).              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the count.                                                   */
    /*------------------------------------------------------------------------*/
    public static int Vcf_CountLonePercent(VoorheesNode container)
    {
        VoorheesItem item;                               // an item looked at
        string sText;                                    // a value's text
        int iCount;                                      // values found so far
        int i;
        int j;

        iCount = 0;
        for (i = 0; i < container.Vn_Count(); i++)
        {
            item = container.Vn_lstItems[i];
            if (!item.Vi_IsValue())
            {   /* A comment. */
                continue;
            }

            if (item.Vi_node.Vn_IsContainer())
            {   /* Its values. */
                iCount += Vcf_CountLonePercent(item.Vi_node);
                continue;
            }

            sText = item.Vi_node.Vn_sText;
            for (j = 0; j < sText.Length; j++)
            {
                if (sText[j] != '%')
                {   /* Not a percent sign. */
                    continue;
                }
                if (j + 1 < sText.Length && (sText[j + 1] == '%' || sText[j + 1] == '('))
                {   /* "%%" or "%(name)s": fine; skip the pair. */
                    j++;
                    continue;
                }
                iCount++;
                break;
            }
        }

        /* Every value with one. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_HasDamagedBlock:                                                   */
    /*                                                                        */
    /* Whether the file holds an auto-saved block's first header line that    */
    /* was not read as a block (it broke the rules: Vcf_ParseBlock), so its   */
    /* lines are plain comments.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat   : the dialect.                                           */
    /*     container : the document node (or a container in it).              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when such a line is a comment.                         */
    /*------------------------------------------------------------------------*/
    public static bool Vcf_HasDamagedBlock(int iFormat, VoorheesNode container)
    {
        VoorheesItem item;                               // an item looked at
        string sHeader;                                  // the block's first line
        int i;

        if (iFormat == VoorheesFormat.VF_KLIPPER)
        {   /* Klipper's SAVE_CONFIG header. */
            sHeader = VCF_SAVECONFIGLINE1;
        }
        else if (iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* KlipperScreen's marker. */
            sHeader = VCF_KSMARKER;
        }
        else
        {   /* Moonraker has none. */
            return(false);
        }

        for (i = 0; i < container.Vn_Count(); i++)
        {
            item = container.Vn_lstItems[i];
            if (item.Vi_iKind == VoorheesItem.VI_COMMENT
                && item.Vi_sComment.TrimEnd(' ', '\t') == sHeader)
            {   /* The header as a plain comment. */
                return(true);
            }

            if (item.Vi_IsValue() && item.Vi_node.Vn_IsContainer()
                && Vcf_HasDamagedBlock(iFormat, item.Vi_node))
            {   /* Inside a section. */
                return(true);
            }
        }

        /* None. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_OpenWarnings:                                                      */
    /*                                                                        */
    /* What the user should be told when a file opens: repeated sections or   */
    /* options (with what the program does with them), a damaged auto-saved   */
    /* block, and KlipperScreen values with a lone "%".                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document just read.                             */
    /*     sFileName    : its file's name (no folder).                        */
    /*     iExtraCopies : repeated-name copies (Vd_ExtraCopies).              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the messages (empty for none).                      */
    /*------------------------------------------------------------------------*/
    public static List<string> Vcf_OpenWarnings(VoorheesDocument doc,
        string sFileName, int iExtraCopies)
    {
        List<string> lstWarnings;                        // the messages
        string sCopies;                                  // "N extra copies"
        int iPercent;                                    // KlipperScreen values with a lone "%"

        lstWarnings = new List<string>();
        if (iExtraCopies > 0)
        {   /* Something repeats: say what the program and Voorhees do with it. */
            sCopies = iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " extra cop";
            if (iExtraCopies == 1)
            {   /* One. */
                sCopies += "y";
            }
            else
            {   /* Several. */
                sCopies += "ies";
            }

            if (doc.Vd_iFormat == VoorheesFormat.VF_KLIPPER)
            {   /* Klipper allows repeats. */
                lstWarnings.Add(sFileName + " repeats a section or an option ("
                    + sCopies + "; section names are compared exactly, option "
                    + "names without case).\r\n\r\nKlipper merges repeated "
                    + "sections and uses the LAST copy of a repeated option.  "
                    + "Voorhees keeps every copy, marks them \"(duplicate)\" "
                    + "in the tree and saves them all back.");
            }
            else
            {   /* Moonraker and KlipperScreen refuse them. */
                lstWarnings.Add(sFileName + " repeats a section or an option ("
                    + sCopies + "; section names are compared exactly, option "
                    + "names without case).\r\n\r\n"
                    + Vcf_ProgramName(doc.Vd_iFormat) + " refuses a file that "
                    + "repeats a section or an option.  Voorhees keeps every "
                    + "copy and marks them \"(duplicate)\" in the tree: delete "
                    + "or rename the copies you do not want.");
            }
        }

        if (Vcf_HasDamagedBlock(doc.Vd_iFormat, doc.Vd_root))
        {   /* The auto-saved block did not read as one. */
            lstWarnings.Add("The auto-saved block in " + sFileName + " is "
                + "damaged: a line in it, or near it, is not in the form "
                + Vcf_ProgramName(doc.Vd_iFormat) + " writes, so "
                + Vcf_ProgramName(doc.Vd_iFormat) + " will not read it as its "
                + "saved settings either.\r\n\r\nVoorhees shows its lines as "
                + "plain comments and saves them back unchanged.");
        }

        if (doc.Vd_iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* KlipperScreen reads "%" as a substitution. */
            iPercent = Vcf_CountLonePercent(doc.Vd_root);
            if (iPercent > 0)
            {   /* Some values would stop it. */
                lstWarnings.Add(iPercent.ToString(CultureInfo.InvariantCulture)
                    + " value(s) in " + sFileName + " hold a single \"%\".  "
                    + "KlipperScreen reads \"%\" as the start of a substitution "
                    + "and reports an error; write \"%%\" for a percent sign.");
            }
        }

        /* Every warning, or none. */
        return(lstWarnings);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_ValidateWarnings:                                                  */
    /*                                                                        */
    /* --validate's one-line warnings: repeats (with the program's rule), a   */
    /* damaged auto-saved block, KlipperScreen's lone "%".                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document read.                                  */
    /*     iExtraCopies : repeated-name copies.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the warnings (empty for none).                      */
    /*------------------------------------------------------------------------*/
    public static List<string> Vcf_ValidateWarnings(VoorheesDocument doc,
        int iExtraCopies)
    {
        List<string> lstWarnings;                        // the warnings
        int iPercent;                                    // KlipperScreen values with a lone "%"

        lstWarnings = new List<string>();
        if (iExtraCopies > 0 && doc.Vd_iFormat == VoorheesFormat.VF_KLIPPER)
        {   /* Klipper: allowed; the last copy wins. */
            lstWarnings.Add(iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " repeated section(s) or option(s); Klipper merges sections "
                + "and uses the last copy of an option");
        }
        else if (iExtraCopies > 0)
        {   /* Moonraker, KlipperScreen: refused. */
            lstWarnings.Add(iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " repeated section(s) or option(s); "
                + Vcf_ProgramName(doc.Vd_iFormat) + " refuses repeats");
        }

        if (Vcf_HasDamagedBlock(doc.Vd_iFormat, doc.Vd_root))
        {   /* The block reads as comments. */
            lstWarnings.Add("the auto-saved block is damaged and reads as plain "
                + "comments");
        }

        if (doc.Vd_iFormat == VoorheesFormat.VF_KLIPPERSCREEN)
        {   /* Lone "%". */
            iPercent = Vcf_CountLonePercent(doc.Vd_root);
            if (iPercent > 0)
            {   /* Some values would stop KlipperScreen. */
                lstWarnings.Add(iPercent.ToString(CultureInfo.InvariantCulture)
                    + " value(s) with a single \"%\" (write \"%%\")");
            }
        }

        /* Every warning, or none. */
        return(lstWarnings);
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_EditWarning:                                                       */
    /*                                                                        */
    /* The once-per-document warning about a change inside the auto-saved     */
    /* block (decision K2): Klipper and KlipperScreen write that block        */
    /* themselves.  A change counts when its container is inside the block,   */
    /* or when it replaces the block itself (a Raw edit); deleting the whole  */
    /* block does not.                                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document (after the change).                            */
    /*     step : the step made, undone or redone.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the message, or null for none.                            */
    /*------------------------------------------------------------------------*/
    public static string Vcf_EditWarning(VoorheesDocument doc,
        VoorheesEditStep step)
    {
        VoorheesChange change;                           // a change looked at
        VoorheesNode root;                               // the document node
        bool bInside;                                    // the change is inside the block
        int i;

        root = doc.Vd_root;
        bInside = false;
        for (i = 0; i < step.Ves_lstChanges.Count && !bInside; i++)
        {
            change = step.Ves_lstChanges[i];
            if (change.Vch_lstPath.Count > 0)
            {   /* Inside a top-level entry: is it the block? */
                bInside = (change.Vch_lstPath[0] < root.Vn_Count()
                    && root.Vn_lstItems[change.Vch_lstPath[0]].Vi_IsValue()
                    && root.Vn_lstItems[change.Vch_lstPath[0]].Vi_node.Vn_iKind
                    == VCF_AUTOSAVE);
            }
            else if (change.Vch_itemOld != null && change.Vch_itemNew != null)
            {   /* A top-level entry replaced: the block itself? */
                bInside = (change.Vch_itemNew.Vi_IsValue()
                    && change.Vch_itemNew.Vi_node.Vn_iKind == VCF_AUTOSAVE);
            }
        }

        if (!bInside)
        {   /* Nothing in the block changed. */
            return(null);
        }

        /* Said once per document. */
        return("This change is inside the auto-saved block, which "
            + Vcf_ProgramName(doc.Vd_iFormat) + " writes itself.\r\n\r\n"
            + Vcf_ProgramName(doc.Vd_iFormat) + " reads the block the next time "
            + "it starts, but its own next save rewrites the whole block from "
            + "what it holds then.  Voorhees keeps the block's form, so it "
            + "stays readable.");
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_DescribeStyle:                                                     */
    /*                                                                        */
    /* --validate --verbose's style summary: delimiter, comment marker,       */
    /* continuation indent.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document read.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "\": \" delimiter, \"#\" comments, 2-space           */
    /*              continuation lines".                                      */
    /*------------------------------------------------------------------------*/
    public static string Vcf_DescribeStyle(VoorheesDocument doc)
    {
        string sIndent;                                  // the continuation indent in words

        if (doc.Vd_sIndentUnit.Trim(' ').Length == 0)
        {   /* Spaces. */
            sIndent = doc.Vd_sIndentUnit.Length.ToString(CultureInfo.InvariantCulture)
                + "-space";
        }
        else
        {   /* Tabs or a mixture. */
            sIndent = "tab";
        }

        /* What new lines will look like. */
        return("\"" + doc.Vd_sDelimiter + "\" delimiter, \"" + doc.Vd_sCommentPrefix
            + "\" comments, " + sIndent + " continuation lines");
    }

    /*------------------------------------------------------------------------*/
    /* Vcf_IncludePath:                                                       */
    /*                                                                        */
    /* The file an include names, for Open Included File: its path read from  */
    /* the including file's folder (Moonraker: an absolute path as it is).    */
    /* It may hold wildcards ("*", "?", "["), which the editor resolves.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document (its file's folder).                           */
    /*     item : the entry.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the full path or pattern, or null when the entry is not   */
    /*              an include or the document has no file yet.               */
    /*------------------------------------------------------------------------*/
    public static string Vcf_IncludePath(VoorheesDocument doc, VoorheesItem item)
    {
        string sFolder;                                  // the including file's folder
        string sPath;                                    // the include's path, with Windows separators

        if (!item.Vi_IsValue() || item.Vi_node.Vn_iKind != VCF_INCLUDE
            || doc.Vd_sPath == null)
        {   /* Not an include, or nowhere to read it from. */
            return(null);
        }

        sFolder = System.IO.Path.GetDirectoryName(doc.Vd_sPath);
        sPath = item.Vi_sKey.Replace('/', '\\');
        if (System.IO.Path.IsPathRooted(sPath))
        {   /* Absolute (a Linux path from Moonraker reads as rooted here). */
            return(sPath);
        }

        /* Beside the including file. */
        return(System.IO.Path.Combine(sFolder, sPath));
    }
}
