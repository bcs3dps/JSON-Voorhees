/*----------------------------------------------------------------------------*/
/* VoorheesReg.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* Windows registry files (.reg), as regedit writes and reads them            */
/* Ground truth: a real "reg export" (two                                     */
/* exports measured 2026-10-06 and 2026-10-07):                               */
/*     o UTF-16 LE with a byte order mark, CRLF;                              */
/*     o first line "Windows Registry Editor Version 5.00" (or "REGEDIT4",    */
/*       whose hex(2) and hex(7) bytes are ANSI, not UTF-16), a blank line;   */
/*     o "[path]" keys ("[-path]" deletes one), each followed by its values,  */
/*       a blank line after each key's values; the file ends with a blank     */
/*       line;                                                                */
/*     o values: name "@" (the default value) or "quoted" with \\ and \"      */
/*       escapes, "=", then "text" (REG_SZ), dword:8 hex digits, hex:bytes    */
/*       (REG_BINARY), hex(2): (EXPAND_SZ), hex(7): (MULTI_SZ), hex(b):       */
/*       (QWORD), any other hex(N):, or "-" (delete the value);               */
/*     o long hex values wrap: after a byte and its comma, once the line is   */
/*       longer than 76 characters, ",\" ends it and the next line starts     */
/*       with two spaces (MEASURED: continuation lines are 78 characters;     */
/*       first lines 79 or 80, depending on the name); strings never wrap.    */
/* ";" lines are comments.  No end-of-line comments.                          */
/*                                                                            */
/* Model, beyond the line engine's (VoorheesLines.cs header):                 */
/*     document: Vn_sRaw holds the header line (written first; not shown in   */
/*               the tree); its first item's lead holds the blank line.       */
/*     key     : VI_MEMBER keyed by the path (no "-"), value a VRG_KEY or     */
/*               VRG_DELETEKEY container; text after the "]" in Vi_sValueGap. */
/*     value   : VI_MEMBER keyed by the name ("" for "@"), Vi_sKeyRaw the     */
/*               quoted name as written when generation would differ,         */
/*               Vi_sKeyGap the "=" (and any spaces round it), value a leaf   */
/*               whose Vn_sRaw is the value exactly as written (hex           */
/*               continuation lines included) and whose Vn_sText is the       */
/*               Value box's text (decision R2: strings as text, numbers as   */
/*               0x..., binary as aa,bb pairs, MULTI_SZ one string a line).   */
/* Values outside any key, and lines that are not keys, values or comments,   */
/* are refused with line and column; so is a malformed value.                 */
/*                                                                            */
/* Voorhees never imports a .reg into the registry.                           */
/*                                                                            */
/* Class:                                                                     */
/*     VoorheesReg (Vrg_) : static functions only; nothing is kept between    */
/*                          calls, so any of them may run on a worker thread. */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*----------------------------------------------------------------------------*/
/* VoorheesReg                                                                */
/*                                                                            */
/* Registry file lines, kinds and rules.                                      */
/*----------------------------------------------------------------------------*/
static class VoorheesReg
{
    /*------------------------------------------------------------------------*/
    /* REG's kinds of node, in its range (section 4).                         */
    /*------------------------------------------------------------------------*/

    public const int VRG_SZ = 40;                        // "text": REG_SZ
    public const int VRG_EXPANDSZ = 41;                  // hex(2): REG_EXPAND_SZ (UTF-16 text with %VARS%)
    public const int VRG_MULTISZ = 42;                   // hex(7): REG_MULTI_SZ (a list of strings)
    public const int VRG_DWORD = 43;                     // dword: REG_DWORD (32 bits)
    public const int VRG_QWORD = 44;                     // hex(b): REG_QWORD (64 bits, little-endian bytes)
    public const int VRG_BINARY = 45;                    // hex: REG_BINARY
    public const int VRG_HEXOTHER = 46;                  // hex(N): any other type, kept as bytes
    public const int VRG_DELETEVALUE = 47;               // "name"=- : delete the value
    public const int VRG_KEY = 48;                       // [path]
    public const int VRG_DELETEKEY = 49;                 // [-path]: delete the key
    const int VRG_INVALID = 59;                          // a malformed value line (never in a document: Vrg_LineProblem refuses it)

    /*------------------------------------------------------------------------*/
    /* The file's furniture, and limits.                                      */
    /*------------------------------------------------------------------------*/

    public const string VRG_HEADER5 = "Windows Registry Editor Version 5.00";  // regedit 5's first line
    public const string VRG_HEADER4 = "REGEDIT4";                              // the old first line (ANSI strings)
    const string VRG_NEWKEYBASE = "HKEY_CURRENT_USER\\Software\\NewKey";       // a new key's path, before 1, 2, 3 ...
    const string VRG_NEWVALUEBASE = "NewValue";                                // a new value's name, likewise
    const int VRG_WRAPAFTER = 76;                                              // a hex line wraps once longer than this (measured)
    const string VRG_WRAPINDENT = "  ";                                        // a hex continuation line's indentation (measured)

    static readonly string[] VRG_HIVES = new string[]
    {
        "HKEY_LOCAL_MACHINE",                            // machine-wide settings
        "HKEY_CURRENT_USER",                             // the user's settings
        "HKEY_CLASSES_ROOT",                             // file types and COM
        "HKEY_USERS",                                    // every loaded user profile
        "HKEY_CURRENT_CONFIG"                            // the current hardware profile
    };

    /*------------------------------------------------------------------------*/
    /* Vrg_HeaderProblem:                                                     */
    /*                                                                        */
    /* Why a file's first line is not a registry file's header, if it is not: */
    /* it must be "Windows Registry Editor Version 5.00" or "REGEDIT4"        */
    /* (trailing spaces allowed, kept as written).                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sLine : the first line (without its line break).                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null for a header.                         */
    /*------------------------------------------------------------------------*/
    public static string Vrg_HeaderProblem(string sLine)
    {
        string sTrim;                                    // the line without trailing spaces

        sTrim = sLine.TrimEnd(' ', '\t');
        if (sTrim == VRG_HEADER5 || sTrim == VRG_HEADER4)
        {   /* A header. */
            return(null);
        }

        /* Anything else. */
        return("not a registry file: its first line must be \"" + VRG_HEADER5
            + "\" (or \"" + VRG_HEADER4 + "\")");
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_IsAnsi:                                                            */
    /*                                                                        */
    /* Whether a document is a REGEDIT4 file, whose hex(2) and hex(7)         */
    /* strings are ANSI bytes rather than UTF-16.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root : the document node (its Vn_sRaw is the header).              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for REGEDIT4.                                          */
    /*------------------------------------------------------------------------*/
    static bool Vrg_IsAnsi(VoorheesNode root)
    {
        /* The old header. */
        return(root.Vn_sRaw != null
            && root.Vn_sRaw.TrimEnd(' ', '\t') == VRG_HEADER4);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ParseLine:                                                         */
    /*                                                                        */
    /* One line as an item: a ";" comment; a "[path]" or "[-path]" key (the   */
    /* path up to the LAST "]"); or a value: "@" or a quoted name, "=", and   */
    /* the value (Vrg_ParseValue).  A line that is none of these comes back   */
    /* as an entry with no key gap, and a malformed value as a VRG_INVALID    */
    /* leaf holding the reason; Vrg_LineProblem reports both.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sContent : the line from its first non-space character to its line */
    /*                break (not empty).                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item; its lead is the line engine's to set.     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vrg_ParseLine(string sContent)
    {
        VoorheesItem item;                               // the item made
        VoorheesNode node;                               // a key's container
        string sName;                                    // a value's decoded name
        string sNameRaw;                                 // its token as written
        int iClose;                                      // a key's "]"
        int iEquals;                                     // a value's "="
        int iValue;                                      // where the value starts

        if (sContent[0] == ';')
        {   /* A comment line, kept whole. */
            return(VoorheesItem.Vi_NewComment(sContent));
        }

        if (sContent[0] == '[')
        {   /* A key, if there is a "]". */
            iClose = sContent.LastIndexOf(']');
            if (iClose > 1)
            {   /* [path] or [-path]. */
                if (sContent[1] == '-')
                {   /* Delete the key. */
                    node = VoorheesNode.Vn_NewContainer(VRG_DELETEKEY);
                    item = VoorheesItem.Vi_NewMember(sContent.Substring(2,
                        iClose - 2), node);
                }
                else
                {   /* The key. */
                    node = VoorheesNode.Vn_NewContainer(VRG_KEY);
                    item = VoorheesItem.Vi_NewMember(sContent.Substring(1,
                        iClose - 1), node);
                }
                node.Vn_sClose = "";
                if (iClose + 1 < sContent.Length)
                {   /* Text after the "]": kept. */
                    item.Vi_sValueGap = sContent.Substring(iClose + 1);
                }
                return(item);
            }
        }

        /* A value: its name first. */
        iEquals = -1;
        sName = null;
        sNameRaw = null;
        if (sContent[0] == '@')
        {   /* The default value. */
            sName = "";
            sNameRaw = "@";
        }
        else if (sContent[0] == '"')
        {   /* A quoted name. */
            sNameRaw = Vrg_QuotedToken(sContent, 0);
            if (sNameRaw != null)
            {   /* Closed: decode it. */
                sName = Vrg_Unquote(sNameRaw);
            }
        }

        if (sNameRaw != null)
        {   /* A name: then "=" (spaces allowed round it). */
            iEquals = sNameRaw.Length;
            while (iEquals < sContent.Length && (sContent[iEquals] == ' '
                || sContent[iEquals] == '\t'))
            {
                iEquals++;
            }
            if (iEquals >= sContent.Length || sContent[iEquals] != '=')
            {   /* No "=": not a value. */
                iEquals = -1;
            }
        }

        if (iEquals < 0)
        {   /* Not a key, a value or a comment: no key gap (Vrg_LineProblem reports it). */
            return(VoorheesItem.Vi_NewMember(sContent,
                VoorheesNode.Vn_NewLeaf(VRG_SZ, "")));
        }

        iValue = iEquals + 1;
        while (iValue < sContent.Length && (sContent[iValue] == ' '
            || sContent[iValue] == '\t'))
        {
            iValue++;
        }

        item = VoorheesItem.Vi_NewMember(sName, Vrg_ParseValue(
            sContent.Substring(iValue)));
        item.Vi_sKeyGap = sContent.Substring(sNameRaw.Length, iValue - sNameRaw.Length);
        if (sName.Length == 0 || sNameRaw != Vrg_QuoteName(sName))
        {   /* "@", or quoted in a way generation would not give back: kept. */
            item.Vi_sKeyRaw = sNameRaw;
        }

        /* Trailing spaces after a string: kept apart from it. */
        if (item.Vi_node.Vn_iKind == VRG_SZ && item.Vi_node.Vn_sRaw != null)
        {   /* The raw string ends at its closing quote; anything after it is the gap. */
            Vrg_SplitTrailing(item);
        }

        /* The value as written. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_SplitTrailing:                                                     */
    /*                                                                        */
    /* Moves what follows a string value's closing quote (spaces) from its    */
    /* raw text into the item's value gap.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the value just parsed (a VRG_SZ leaf with a raw token).     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the raw ends at the quote; the rest is in Vi_sValueGap.     */
    /*------------------------------------------------------------------------*/
    static void Vrg_SplitTrailing(VoorheesItem item)
    {
        string sToken;                                   // the quoted string

        sToken = Vrg_QuotedToken(item.Vi_node.Vn_sRaw, 0);
        if (sToken != null && sToken.Length < item.Vi_node.Vn_sRaw.Length)
        {   /* Something after it. */
            item.Vi_sValueGap = item.Vi_node.Vn_sRaw.Substring(sToken.Length);
            item.Vi_node.Vn_sRaw = sToken;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_QuotedToken:                                                       */
    /*                                                                        */
    /* A double-quoted token at a position, through its closing quote (a      */
    /* backslash escapes the next character).                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s      : the text.                                                 */
    /*     iStart : where the opening quote is.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the token, quotes included, or null when never closed.    */
    /*------------------------------------------------------------------------*/
    static string Vrg_QuotedToken(string s, int iStart)
    {
        int i;

        for (i = iStart + 1; i < s.Length; i++)
        {
            if (s[i] == '\\')
            {   /* An escape: skip the next character. */
                i++;
            }
            else if (s[i] == '"')
            {   /* The closing quote. */
                return(s.Substring(iStart, i + 1 - iStart));
            }
        }

        /* Never closed. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_Unquote:                                                           */
    /*                                                                        */
    /* A quoted token's text: the quotes removed, "\\" and "\"" decoded (any  */
    /* other backslash kept as it is, as regedit does).                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sToken : the token, quotes included.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text.                                                 */
    /*------------------------------------------------------------------------*/
    static string Vrg_Unquote(string sToken)
    {
        StringBuilder sb;                                // the text
        int i;

        sb = new StringBuilder();
        for (i = 1; i < sToken.Length - 1; i++)
        {
            if (sToken[i] == '\\' && i + 1 < sToken.Length - 1
                && (sToken[i + 1] == '\\' || sToken[i + 1] == '"'))
            {   /* An escape: the character after it. */
                i++;
            }
            sb.Append(sToken[i]);
        }

        /* The decoded text. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_Quote:                                                             */
    /*                                                                        */
    /* Text as a quoted token: backslashes and quotes escaped.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : "\"" + escaped text + "\"".                               */
    /*------------------------------------------------------------------------*/
    static string Vrg_Quote(string sText)
    {
        /* Backslashes first, so the quotes' escapes are not doubled. */
        return("\"" + sText.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_QuoteName:                                                         */
    /*                                                                        */
    /* A value name as regedit writes it: "@" for the default (""), else      */
    /* quoted.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sName : the name.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the token.                                                */
    /*------------------------------------------------------------------------*/
    static string Vrg_QuoteName(string sName)
    {
        if (sName.Length == 0)
        {   /* The default value. */
            return("@");
        }

        /* Quoted. */
        return(Vrg_Quote(sName));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ParseValue:                                                        */
    /*                                                                        */
    /* A value's text after its "=" (its first line; hex continuation lines   */
    /* are added by Vrg_AddContinuation): its kind from its form, its raw     */
    /* text as written, its Value box text (Vrg_TextFromRaw).  A malformed    */
    /* value is a VRG_INVALID leaf whose text is the reason.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the value as written.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value.                                          */
    /*------------------------------------------------------------------------*/
    static VoorheesNode Vrg_ParseValue(string sRaw)
    {
        VoorheesNode node;                               // the value
        string sError;                                   // why it is malformed
        string sText;                                    // its Value box text
        int iKind;                                       // its kind

        /* A malformed value keeps its raw text too: a wrapped hex value's   */
        /* first line is malformed until its continuation lines arrive       */
        /* (Vrg_AddContinuation reads it all again).                         */
        iKind = Vrg_KindOfRaw(sRaw);
        if (iKind == VRG_INVALID)
        {   /* Not a form regedit knows. */
            node = VoorheesNode.Vn_NewLeaf(VRG_INVALID, "the value is not "
                + "\"text\", dword:, hex:, hex(N): or -");
            node.Vn_sRaw = sRaw;
            return(node);
        }

        sText = Vrg_TextFromRaw(iKind, sRaw, false, out sError);
        if (sText == null)
        {   /* Its form is known but its contents are not valid (yet). */
            node = VoorheesNode.Vn_NewLeaf(VRG_INVALID, sError);
            node.Vn_sRaw = sRaw;
            return(node);
        }

        node = VoorheesNode.Vn_NewLeaf(iKind, sText);
        node.Vn_sRaw = sRaw;

        /* The value as written and as shown. */
        return(node);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_KindOfRaw:                                                         */
    /*                                                                        */
    /* A value's kind from how it is written: "..." SZ, - DELETEVALUE,        */
    /* dword: DWORD, hex: BINARY, hex(2): EXPANDSZ, hex(7): MULTISZ, hex(b):  */
    /* QWORD, any other hex(N): HEXOTHER.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the value as written.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the VRG_ kind, or VRG_INVALID.                               */
    /*------------------------------------------------------------------------*/
    static int Vrg_KindOfRaw(string sRaw)
    {
        string sLower;                                   // the start, lower case
        string sType;                                    // a hex(N)'s N

        if (sRaw.StartsWith("\"", StringComparison.Ordinal))
        {   /* A string. */
            return(VRG_SZ);
        }

        if (sRaw.TrimEnd(' ', '\t') == "-")
        {   /* Delete the value. */
            return(VRG_DELETEVALUE);
        }

        sLower = sRaw.ToLowerInvariant();
        if (sLower.StartsWith("dword:", StringComparison.Ordinal))
        {   /* 32 bits. */
            return(VRG_DWORD);
        }

        if (sLower.StartsWith("hex:", StringComparison.Ordinal))
        {   /* Bytes. */
            return(VRG_BINARY);
        }

        if (sLower.StartsWith("hex(", StringComparison.Ordinal) && sLower.IndexOf("):",
            StringComparison.Ordinal) > 4)
        {   /* hex(N): by its type number. */
            sType = sLower.Substring(4, sLower.IndexOf("):", StringComparison.Ordinal) - 4);
            if (sType == "2")
            {   /* REG_EXPAND_SZ. */
                return(VRG_EXPANDSZ);
            }
            if (sType == "7")
            {   /* REG_MULTI_SZ. */
                return(VRG_MULTISZ);
            }
            if (sType == "b")
            {   /* REG_QWORD. */
                return(VRG_QWORD);
            }
            return(VRG_HEXOTHER);
        }

        /* Not a form regedit writes. */
        return(VRG_INVALID);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_HexBytes:                                                          */
    /*                                                                        */
    /* The bytes of a hex value's text after its type prefix: pairs of hex    */
    /* digits separated by commas, with "\" line ends, line breaks and        */
    /* spaces between them ignored (as regedit reads them).                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sHex   : the text after "hex:" or "hex(N):".                       */
    /*     sError : set to why it is not valid, else null.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the bytes, or null when not valid.                        */
    /*------------------------------------------------------------------------*/
    static byte[] Vrg_HexBytes(string sHex, out string sError)
    {
        List<byte> lstBytes;                             // the bytes
        string[] arrParts;                               // the comma-separated pairs
        string sPart;                                    // one of them, cleaned
        int iValue;                                      // its value
        int i;

        sError = null;
        lstBytes = new List<byte>();
        arrParts = sHex.Replace("\\", "").Replace("\r", "").Replace("\n", "")
            .Replace(" ", "").Replace("\t", "").Split(',');
        for (i = 0; i < arrParts.Length; i++)
        {
            sPart = arrParts[i];
            if (sPart.Length == 0 && arrParts.Length == 1)
            {   /* No bytes at all. */
                break;
            }
            if (sPart.Length == 0 || sPart.Length > 2 || !int.TryParse(sPart,
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out iValue))
            {   /* Not a hex byte. */
                sError = "\"" + sPart + "\" is not a hex byte (two hex digits "
                    + "between commas)";
                return(null);
            }
            lstBytes.Add((byte)iValue);
        }

        /* Every byte. */
        return(lstBytes.ToArray());
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_PairsText:                                                         */
    /*                                                                        */
    /* Bytes as the Value box shows them: lower-case hex pairs and commas.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes : the bytes.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "0a,ff,00" ("" for none).                            */
    /*------------------------------------------------------------------------*/
    static string Vrg_PairsText(byte[] bytes)
    {
        StringBuilder sb;                                // the text
        int i;

        sb = new StringBuilder();
        for (i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {   /* Between pairs. */
                sb.Append(',');
            }
            sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        /* The pairs. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_StringEncoding:                                                    */
    /*                                                                        */
    /* How hex(2) and hex(7) strings are stored: UTF-16 LE, or the system's   */
    /* ANSI code page in a REGEDIT4 file.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bAnsi : a REGEDIT4 file.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Encoding : the encoding.                                           */
    /*------------------------------------------------------------------------*/
    static Encoding Vrg_StringEncoding(bool bAnsi)
    {
        if (bAnsi)
        {   /* REGEDIT4: single bytes. */
            return(Encoding.Default);
        }

        /* regedit 5: UTF-16 little-endian. */
        return(new UnicodeEncoding(false, false));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_TextFromRaw:                                                       */
    /*                                                                        */
    /* A value's Value box text from how it is written (decision R2):         */
    /*     SZ        the string, decoded;                                     */
    /*     DWORD     "0x" + 8 hex digits (fewer digits accepted on read);     */
    /*     QWORD     "0x" + 16 hex digits, from 8 little-endian bytes;        */
    /*     BINARY    hex pairs "aa,bb";                                       */
    /*     EXPANDSZ  the string, from UTF-16 (ANSI for REGEDIT4) less its     */
    /*               final NUL;                                               */
    /*     MULTISZ   one string per line (the final empty strings dropped);   */
    /*     HEXOTHER  "hex(N):" + its pairs;                                   */
    /*     DELETE    "".                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind  : the value's kind.                                         */
    /*     sRaw   : the value as written.                                     */
    /*     bAnsi  : a REGEDIT4 file.                                          */
    /*     sError : set to why it is not valid, else null.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text, or null when not valid.                         */
    /*------------------------------------------------------------------------*/
    static string Vrg_TextFromRaw(int iKind, string sRaw, bool bAnsi,
        out string sError)
    {
        string sToken;                                   // a string's token
        string sDigits;                                  // a dword's digits
        string sText;                                    // decoded string data
        byte[] bytes;                                    // a hex value's bytes
        uint uValue;                                     // a dword
        ulong ulValue;                                   // a qword
        int iColon;                                      // the end of a hex prefix
        int i;

        sError = null;
        switch (iKind)
        {
            case VRG_SZ:
                /* A quoted string, closed. */
                sToken = Vrg_QuotedToken(sRaw, 0);
                if (sToken == null)
                {   /* Never closed. */
                    sError = "the string's closing quote is missing";
                    return(null);
                }
                return(Vrg_Unquote(sToken));

            case VRG_DELETEVALUE:
                /* Nothing to show. */
                return("");

            case VRG_DWORD:
                /* 1 to 8 hex digits. */
                sDigits = sRaw.Substring(6).Trim(' ', '\t');
                if (sDigits.Length == 0 || sDigits.Length > 8 || !uint.TryParse(sDigits,
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                    out uValue))
                {   /* Not a dword. */
                    sError = "dword: needs 1 to 8 hex digits";
                    return(null);
                }
                return("0x" + uValue.ToString("x8", CultureInfo.InvariantCulture));

            default:
                /* A hex form: its bytes first. */
                break;
        }

        iColon = sRaw.IndexOf(':');
        bytes = Vrg_HexBytes(sRaw.Substring(iColon + 1), out sError);
        if (bytes == null)
        {   /* Not valid hex. */
            return(null);
        }

        switch (iKind)
        {
            case VRG_QWORD:
                /* Up to 8 little-endian bytes. */
                if (bytes.Length > 8)
                {   /* Too many. */
                    sError = "hex(b): holds at most 8 bytes";
                    return(null);
                }
                ulValue = 0;
                for (i = bytes.Length - 1; i >= 0; i--)
                {
                    ulValue = (ulValue << 8) | bytes[i];
                }
                return("0x" + ulValue.ToString("x16", CultureInfo.InvariantCulture));

            case VRG_EXPANDSZ:
                /* One string, less its final NUL. */
                sText = Vrg_StringEncoding(bAnsi).GetString(bytes);
                if (sText.EndsWith("\0", StringComparison.Ordinal))
                {   /* The terminator. */
                    sText = sText.Substring(0, sText.Length - 1);
                }
                return(sText);

            case VRG_MULTISZ:
                /* Strings separated by NULs, the list ended by an empty one. */
                sText = Vrg_StringEncoding(bAnsi).GetString(bytes);
                while (sText.EndsWith("\0", StringComparison.Ordinal))
                {   /* The terminators. */
                    sText = sText.Substring(0, sText.Length - 1);
                }
                return(sText.Replace('\0', '\n'));

            case VRG_HEXOTHER:
                /* Its type kept, its bytes as pairs. */
                return(sRaw.Substring(0, iColon + 1).ToLowerInvariant()
                    + Vrg_PairsText(bytes));

            default:
                /* BINARY: the pairs. */
                return(Vrg_PairsText(bytes));
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ContinuesAfter:                                                    */
    /*                                                                        */
    /* Whether a value's line goes on to the next one: a hex value whose line */
    /* ends with "\" (regedit's wrapping).  Strings never wrap.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item  : the value item.                                            */
    /*     sLine : the line so far (its last line).                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the next line belongs to it.                      */
    /*------------------------------------------------------------------------*/
    public static bool Vrg_ContinuesAfter(VoorheesItem item, string sLine)
    {
        int iKind;                                       // the value's kind (from its raw form)

        if (item.Vi_node.Vn_sRaw == null)
        {   /* Not a value read from the file. */
            return(false);
        }

        iKind = item.Vi_node.Vn_iKind;
        if (iKind == VRG_INVALID && item.Vi_sKeyGap != null)
        {   /* Possibly a hex value whose bytes run on: judge by the line itself. */
            iKind = VRG_BINARY;
        }

        /* A hex form whose line ends in a backslash. */
        return(iKind != VRG_SZ && iKind != VRG_DWORD && iKind != VRG_DELETEVALUE
            && item.Vi_sKeyGap != null && sLine.TrimEnd(' ', '\t').EndsWith("\\",
            StringComparison.Ordinal));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_AddContinuation:                                                   */
    /*                                                                        */
    /* Adds a hex value's continuation lines to it: the raw value takes them  */
    /* exactly as written, and the Value box text is worked out again from    */
    /* all of it.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item  : the value.                                                 */
    /*     sMore : the continuation text (it starts with a line break).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value is replaced by its whole form.                    */
    /*------------------------------------------------------------------------*/
    public static void Vrg_AddContinuation(VoorheesItem item, string sMore)
    {
        string sFirst;                                   // the first line's value as written

        sFirst = item.Vi_node.Vn_sRaw;
        if (sFirst == null)
        {   /* Never: a parsed value always keeps its raw text. */
            sFirst = "";
        }

        /* The whole value, read again (it may now be valid, or still not). */
        item.Vi_node = Vrg_ParseValue(sFirst + sMore);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_LineProblem:                                                       */
    /*                                                                        */
    /* Why a line read from a file (or the Raw box) is refused, if it is:     */
    /* a line that is not a key, a value or a comment; a malformed value; a   */
    /* value outside any key; a value under a key being deleted.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item  : the item the line made.                                    */
    /*     under : the key's container it falls under, or null before the     */
    /*             first key.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason (to follow "Line L, column C: "), or null.     */
    /*------------------------------------------------------------------------*/
    public static string Vrg_LineProblem(VoorheesItem item, VoorheesNode under)
    {
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT || item.Vi_node.Vn_IsContainer())
        {   /* A comment or a key: fine. */
            return(null);
        }

        if (item.Vi_sKeyGap == null)
        {   /* No name and "=". */
            return("this line is not a [key], a value (\"name\"=... or @=...) "
                + "or a ; comment");
        }

        if (item.Vi_node.Vn_iKind == VRG_INVALID)
        {   /* A malformed value: its reason. */
            return(item.Vi_node.Vn_sText);
        }

        if (under == null)
        {   /* Before the first key. */
            return("a value must be inside a key: put a [HKEY_...] line above it");
        }

        if (under.Vn_iKind == VRG_DELETEKEY)
        {   /* Under [-key]. */
            return("a key being deleted ([-...]) holds no values");
        }

        /* A valid value. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_WriteEntry:                                                        */
    /*                                                                        */
    /* A value's line(s): its name ("@", or quoted: as written when it was),  */
    /* the "=" (with its spaces as written), the value (as written, or        */
    /* generated in regedit's form: Vrg_GenerateValue), trailing spaces.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc   : the document (line ending, REGEDIT4).                      */
    /*     item  : the value.                                                 */
    /*     sLead : the lead written before it (its indentation counts toward  */
    /*             a hex value's wrap column).                                */
    /*     sb    : where the text goes.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the text is appended.                                       */
    /*------------------------------------------------------------------------*/
    public static void Vrg_WriteEntry(VoorheesDocument doc, VoorheesItem item,
        string sLead, StringBuilder sb)
    {
        string sName;                                    // the name as written
        string sGap;                                     // name to value

        if (item.Vi_sKeyRaw != null)
        {   /* As written. */
            sName = item.Vi_sKeyRaw;
        }
        else
        {   /* Generated. */
            sName = Vrg_QuoteName(item.Vi_sKey);
        }

        if (item.Vi_sKeyGap != null)
        {   /* As written. */
            sGap = item.Vi_sKeyGap;
        }
        else
        {   /* regedit's. */
            sGap = "=";
        }

        sb.Append(sName);
        sb.Append(sGap);
        if (item.Vi_node.Vn_sRaw != null)
        {   /* As written. */
            sb.Append(item.Vi_node.Vn_sRaw);
        }
        else
        {   /* Generated, wrapped from the column where it starts. */
            sb.Append(Vrg_GenerateValue(doc, item.Vi_node, Vrg_OwnIndent(sLead).Length
                + sName.Length + sGap.Length));
        }

        if (item.Vi_sValueGap != null)
        {   /* Trailing text as written. */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_OwnIndent:                                                         */
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
    static string Vrg_OwnIndent(string sLead)
    {
        if (sLead == null)
        {   /* No lead known. */
            return("");
        }

        /* After the last break. */
        return(sLead.Substring(sLead.LastIndexOfAny(new char[] { '\r', '\n' }) + 1));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_GenerateValue:                                                     */
    /*                                                                        */
    /* A value in regedit's own form, from its text: SZ quoted with escapes;  */
    /* DWORD "dword:" + 8 hex digits; the hex forms "hex:" / "hex(N):" + the  */
    /* bytes as comma-separated pairs, wrapped as regedit wraps them (after a */
    /* byte and its comma, once the line is longer than 76 characters: ",\",  */
    /* a line break, two spaces); DELETEVALUE "-".                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document (line ending, REGEDIT4 strings).            */
    /*     node    : the value (its text valid for its kind).                 */
    /*     iColumn : the column the value starts at (the line's text before   */
    /*               it).                                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the value as written.                                     */
    /*------------------------------------------------------------------------*/
    static string Vrg_GenerateValue(VoorheesDocument doc, VoorheesNode node,
        int iColumn)
    {
        StringBuilder sb;                                // the value
        string sPrefix;                                  // a hex form's type prefix
        byte[] bytes;                                    // its bytes
        int iLineLength;                                 // the current line's length so far
        int i;

        if (node.Vn_iKind == VRG_SZ)
        {   /* A string. */
            return(Vrg_Quote(node.Vn_sText));
        }

        if (node.Vn_iKind == VRG_DELETEVALUE)
        {   /* Delete it. */
            return("-");
        }

        if (node.Vn_iKind == VRG_DWORD)
        {   /* Eight hex digits. */
            return("dword:" + Vrg_ParseNumber(node.Vn_sText, false).ToString("x8",
                CultureInfo.InvariantCulture));
        }

        bytes = Vrg_BytesOfText(node.Vn_iKind, node.Vn_sText,
            Vrg_IsAnsi(doc.Vd_root), out sPrefix);
        sb = new StringBuilder();
        sb.Append(sPrefix);
        iLineLength = iColumn + sPrefix.Length;
        for (i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            iLineLength += 2;
            if (i == bytes.Length - 1)
            {   /* The last byte: no comma. */
                break;
            }
            sb.Append(',');
            iLineLength++;
            if (iLineLength > VRG_WRAPAFTER)
            {   /* Past the wrap column with more to come: continue on the next line. */
                sb.Append('\\');
                sb.Append(doc.Vd_sNewline);
                sb.Append(VRG_WRAPINDENT);
                iLineLength = VRG_WRAPINDENT.Length;
            }
        }

        /* The hex value, wrapped. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ParseNumber:                                                       */
    /*                                                                        */
    /* A DWORD's or QWORD's text as a number: "0x" + hex digits, or decimal.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText  : the text (already checked by Vrg_NumberProblem).          */
    /*     bQword : 64 bits rather than 32.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     ulong : the number (0 when it does not read).                      */
    /*------------------------------------------------------------------------*/
    static ulong Vrg_ParseNumber(string sText, bool bQword)
    {
        ulong ulValue;                                   // the number
        string sTrim;                                    // the text trimmed

        sTrim = sText.Trim();
        if (sTrim.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {   /* Hex. */
            if (!ulong.TryParse(sTrim.Substring(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out ulValue))
            {   /* Not hex (refused before this is reached). */
                ulValue = 0;
            }
        }
        else if (!ulong.TryParse(sTrim, NumberStyles.None, CultureInfo.InvariantCulture,
            out ulValue))
        {   /* Not decimal either. */
            ulValue = 0;
        }

        if (!bQword && ulValue > uint.MaxValue)
        {   /* Too big for a DWORD (refused before this is reached). */
            ulValue = 0;
        }

        /* The number. */
        return(ulValue);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NumberProblem:                                                     */
    /*                                                                        */
    /* Why text is not a DWORD or QWORD, if it is not: "0x" + 1 to 8 (16) hex */
    /* digits, or a decimal number that fits.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText  : the text.                                                 */
    /*     bQword : 64 bits rather than 32.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null.                                      */
    /*------------------------------------------------------------------------*/
    static string Vrg_NumberProblem(string sText, bool bQword)
    {
        ulong ulValue;                                   // the number
        string sTrim;                                    // the text trimmed
        string sWhat;                                    // DWORD or QWORD
        int iMaxDigits;                                  // 8 or 16

        sTrim = sText.Trim();
        sWhat = "A DWORD";
        iMaxDigits = 8;
        if (bQword)
        {   /* 64 bits. */
            sWhat = "A QWORD";
            iMaxDigits = 16;
        }

        if (sTrim.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {   /* Hex. */
            if (sTrim.Length < 3 || sTrim.Length - 2 > iMaxDigits || !ulong.TryParse(
                sTrim.Substring(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out ulValue))
            {   /* Not 1 to N hex digits. */
                return(sWhat + " is 0x and 1 to " + iMaxDigits.ToString(
                    CultureInfo.InvariantCulture) + " hex digits, or a decimal number.");
            }
            return(null);
        }

        if (!ulong.TryParse(sTrim, NumberStyles.None, CultureInfo.InvariantCulture,
            out ulValue) || (!bQword && ulValue > uint.MaxValue))
        {   /* Not a decimal that fits. */
            return(sWhat + " is 0x and hex digits, or a decimal number from 0 to "
                + (bQword ? "18446744073709551615" : "4294967295") + ".");
        }

        /* A number. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_BytesOfText:                                                       */
    /*                                                                        */
    /* A hex value's bytes and type prefix, from its Value box text (already  */
    /* valid: Vrg_ValueProblem): QWORD 8 little-endian bytes; EXPANDSZ the    */
    /* string and a NUL; MULTISZ each line and a NUL, then a NUL; BINARY the  */
    /* pairs; HEXOTHER its "hex(N):" and pairs.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind   : the kind.                                                */
    /*     sText   : the text.                                                */
    /*     bAnsi   : a REGEDIT4 file (strings as ANSI bytes).                 */
    /*     sPrefix : set to "hex:" or "hex(N):".                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the bytes.                                                */
    /*------------------------------------------------------------------------*/
    static byte[] Vrg_BytesOfText(int iKind, string sText, bool bAnsi,
        out string sPrefix)
    {
        byte[] bytes;                                    // the bytes
        string sError;                                   // (valid text: unused)
        string sLines;                                   // MULTISZ lines with NULs
        ulong ulValue;                                   // a qword
        int iColon;                                      // a HEXOTHER prefix's end
        int i;

        switch (iKind)
        {
            case VRG_QWORD:
                /* 8 bytes, least significant first. */
                sPrefix = "hex(b):";
                ulValue = Vrg_ParseNumber(sText, true);
                bytes = new byte[8];
                for (i = 0; i < 8; i++)
                {
                    bytes[i] = (byte)(ulValue >> (8 * i));
                }
                return(bytes);

            case VRG_EXPANDSZ:
                /* The string and its NUL. */
                sPrefix = "hex(2):";
                return(Vrg_StringEncoding(bAnsi).GetBytes(sText.Replace("\r\n", "\n")
                    + "\0"));

            case VRG_MULTISZ:
                /* Each line and a NUL, then the list's NUL. */
                sPrefix = "hex(7):";
                sLines = sText.Replace("\r\n", "\n").Replace('\r', '\n');
                if (sLines.Length == 0)
                {   /* An empty list: just the terminator. */
                    return(Vrg_StringEncoding(bAnsi).GetBytes("\0"));
                }
                return(Vrg_StringEncoding(bAnsi).GetBytes(sLines.Replace('\n', '\0')
                    + "\0\0"));

            case VRG_HEXOTHER:
                /* Its own type, its pairs. */
                iColon = sText.IndexOf(':');
                sPrefix = sText.Substring(0, iColon + 1).ToLowerInvariant();
                return(Vrg_HexBytes(sText.Substring(iColon + 1), out sError));

            default:
                /* BINARY: the pairs (commas or spaces between them). */
                sPrefix = "hex:";
                return(Vrg_HexBytes(Vrg_PairsInput(sText), out sError));
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_PairsInput:                                                        */
    /*                                                                        */
    /* Typed binary as comma-separated pairs: spaces, tabs and line breaks    */
    /* between pairs are taken as commas.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the typed text.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the pairs with commas.                                    */
    /*------------------------------------------------------------------------*/
    static string Vrg_PairsInput(string sText)
    {
        string[] arrParts;                               // the pieces between separators
        List<string> lstPairs;                           // the non-empty ones

        arrParts = sText.Split(new char[] { ',', ' ', '\t', '\r', '\n' });
        lstPairs = new List<string>();
        foreach (string sPart in arrParts)
        {
            if (sPart.Length > 0)
            {   /* A pair. */
                lstPairs.Add(sPart);
            }
        }

        /* Comma-separated. */
        return(string.Join(",", lstPairs.ToArray()));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ValueProblem:                                                      */
    /*                                                                        */
    /* Why text cannot be a value of a kind, if it cannot: SZ one line        */
    /* (regedit keeps line breaks only as hex(1)); DWORD / QWORD numbers;     */
    /* BINARY hex pairs; HEXOTHER "hex(N):" and pairs; EXPANDSZ one line;     */
    /* MULTISZ any lines.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : the kind.                                                  */
    /*     sText : the text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null.                                      */
    /*------------------------------------------------------------------------*/
    public static string Vrg_ValueProblem(int iKind, string sText)
    {
        string sError;                                   // a hex problem
        int iColon;                                      // a HEXOTHER prefix's end

        switch (iKind)
        {
            case VRG_SZ:
            case VRG_EXPANDSZ:
                /* One line. */
                if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
                {   /* Several lines. */
                    return("A registry string is one line (regedit stores line "
                        + "breaks only in hex form); use a Multi-String for a list.");
                }
                return(null);

            case VRG_DWORD:
                /* 32 bits. */
                return(Vrg_NumberProblem(sText, false));

            case VRG_QWORD:
                /* 64 bits. */
                return(Vrg_NumberProblem(sText, true));

            case VRG_BINARY:
                /* Hex pairs. */
                if (Vrg_HexBytes(Vrg_PairsInput(sText), out sError) == null)
                {   /* Not pairs. */
                    return("Binary data is hex pairs separated by commas or "
                        + "spaces: " + sError + ".");
                }
                return(null);

            case VRG_HEXOTHER:
                /* hex(N): and pairs. */
                iColon = sText.IndexOf(':');
                if (!sText.StartsWith("hex(", StringComparison.OrdinalIgnoreCase)
                    || iColon < 6 || sText[iColon - 1] != ')')
                {   /* Not hex(N):. */
                    return("This value is written hex(N): followed by hex pairs.");
                }
                if (Vrg_HexBytes(sText.Substring(iColon + 1), out sError) == null)
                {   /* Not pairs. */
                    return("This value is written hex(N): followed by hex pairs: "
                        + sError + ".");
                }
                return(null);

            default:
                /* MULTISZ, DELETEVALUE: anything. */
                return(null);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_MakeValue:                                                         */
    /*                                                                        */
    /* A value of a kind from text (the Value box, in-place editing,          */
    /* Replace), its text in the Value box's own form (numbers as 0x...,      */
    /* pairs lower case and comma-separated), or why it can't be one.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind  : the kind.                                                 */
    /*     sText  : the text.                                                 */
    /*     sError : set to why it is refused, else null.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value (to be written in regedit's form), or     */
    /*                    null when refused.                                  */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vrg_MakeValue(int iKind, string sText,
        out string sError)
    {
        string sValue;                                   // the text in the Value box's form
        string sPrefix;                                  // (unused)
        byte[] bytes;                                    // binary bytes

        sError = Vrg_ValueProblem(iKind, sText);
        if (sError != null)
        {   /* Refused. */
            return(null);
        }

        sValue = sText.Replace("\r\n", "\n").Replace('\r', '\n');
        if (iKind == VRG_DWORD)
        {   /* As 0x and eight digits. */
            sValue = "0x" + Vrg_ParseNumber(sText, false).ToString("x8",
                CultureInfo.InvariantCulture);
        }
        else if (iKind == VRG_QWORD)
        {   /* As 0x and sixteen digits. */
            sValue = "0x" + Vrg_ParseNumber(sText, true).ToString("x16",
                CultureInfo.InvariantCulture);
        }
        else if (iKind == VRG_BINARY)
        {   /* As pairs. */
            bytes = Vrg_BytesOfText(iKind, sText, false, out sPrefix);
            sValue = Vrg_PairsText(bytes);
        }
        else if (iKind == VRG_DELETEVALUE)
        {   /* Nothing. */
            sValue = "";
        }

        /* The value. */
        return(VoorheesNode.Vn_NewLeaf(iKind, sValue));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_LoadDocument:                                                      */
    /*                                                                        */
    /* A whole file's bytes to a registry document: decoded, read by the line */
    /* engine (the header first; refused with line and column where regedit   */
    /* would not take it), its hex(2) / hex(7) strings read again as ANSI for */
    /* a REGEDIT4 file.                                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes  : the file's contents.                                      */
    /*     sPath  : the file, for the document to remember; may be null.      */
    /*     sError : set to why the file is refused, else null.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document, or null when refused.             */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vrg_LoadDocument(byte[] bytes, string sPath,
        out string sError)
    {
        Encoding encoding;                               // the file's encoding
        bool bBom;                                       // it had a byte order mark
        string sText;                                    // the decoded file
        string sNewline;                                 // its line ending
        VoorheesNode root;                               // the document node

        sText = VoorheesText.Vtx_Decode(bytes, out encoding, out bBom);
        root = VoorheesLines.Vln_Parse(VoorheesFormat.VF_REG, sText, null,
            out sError);
        if (root == null)
        {   /* Refused (sError has line and column). */
            return(null);
        }

        if (Vrg_IsAnsi(root))
        {   /* REGEDIT4: its strings in hex are ANSI. */
            Vrg_RereadStrings(root);
        }

        sNewline = "\r\n";
        if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
        {   /* Line breaks to go by. */
            sNewline = VoorheesText.Vtx_DetectNewline(sText);
        }

        /* The document, its delimiter and marker regedit's. */
        return(VoorheesDocument.Vd_Create(VoorheesFormat.VF_REG, root, sPath,
            encoding, bBom, sNewline, null, null, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_RereadStrings:                                                     */
    /*                                                                        */
    /* In a REGEDIT4 file, the Value box text of every hex(2) and hex(7)      */
    /* value read again with ANSI bytes.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the document node (or a key).                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the values' texts are replaced.                             */
    /*------------------------------------------------------------------------*/
    static void Vrg_RereadStrings(VoorheesNode container)
    {
        VoorheesNode node;                               // a value
        string sError;                                   // (valid already: unused)
        string sText;                                    // its text read as ANSI
        int i;

        for (i = 0; i < container.Vn_Count(); i++)
        {
            node = container.Vn_lstItems[i].Vi_node;
            if (node == null)
            {   /* A comment. */
                continue;
            }

            if (node.Vn_IsContainer())
            {   /* A key: its values. */
                Vrg_RereadStrings(node);
            }
            else if ((node.Vn_iKind == VRG_EXPANDSZ || node.Vn_iKind == VRG_MULTISZ)
                && node.Vn_sRaw != null)
            {   /* A string in hex: ANSI. */
                sText = Vrg_TextFromRaw(node.Vn_iKind, node.Vn_sRaw, true, out sError);
                if (sText != null)
                {   /* Read. */
                    node.Vn_sText = sText;
                }
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_SaveBytes:                                                         */
    /*                                                                        */
    /* A registry document to the bytes of its file, in its own encoding.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the file's new contents.                                  */
    /*------------------------------------------------------------------------*/
    public static byte[] Vrg_SaveBytes(VoorheesDocument doc)
    {
        /* Text by the line engine, bytes as the file had them. */
        return(VoorheesText.Vtx_Encode(VoorheesLines.Vln_Write(doc),
            doc.Vd_encoding, doc.Vd_bBom));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NewDocument:                                                       */
    /*                                                                        */
    /* A new registry document (decision R3): UTF-16 LE with a byte order     */
    /* mark, CRLF, the header line, no keys yet.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document.                                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vrg_NewDocument()
    {
        VoorheesNode root;                               // the document node with its header

        root = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);
        root.Vn_bOneLine = false;
        root.Vn_sRaw = VRG_HEADER5;

        /* regedit's own encoding and line ending. */
        return(VoorheesDocument.Vd_Create(VoorheesFormat.VF_REG, root, null,
            new UnicodeEncoding(false, true), true, "\r\n", null, null, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_KindName:                                                          */
    /*                                                                        */
    /* A kind's name, for the Type box, menus and messages.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VRG_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name; "Value" for anything else.                      */
    /*------------------------------------------------------------------------*/
    public static string Vrg_KindName(int iKind)
    {
        switch (iKind)
        {
            case VRG_SZ:          return("String");                  // REG_SZ
            case VRG_EXPANDSZ:    return("Expandable String");       // REG_EXPAND_SZ
            case VRG_MULTISZ:     return("Multi-String");            // REG_MULTI_SZ
            case VRG_DWORD:       return("DWORD");                   // REG_DWORD
            case VRG_QWORD:       return("QWORD");                   // REG_QWORD
            case VRG_BINARY:      return("Binary");                  // REG_BINARY
            case VRG_HEXOTHER:    return("Other Hex");               // hex(N)
            case VRG_DELETEVALUE: return("Delete Value");            // "name"=-
            case VRG_KEY:         return("Key");                     // [path]
            case VRG_DELETEKEY:   return("Delete Key");              // [-path]
            default:              return("Value");                   // not a REG kind
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ChildKinds:                                                        */
    /*                                                                        */
    /* What Add Child offers: keys (and keys to delete) at the top level;     */
    /* every value kind but Other Hex in a key; nothing but comments under a  */
    /* key being deleted.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds, in menu order (a new list).                 */
    /*------------------------------------------------------------------------*/
    public static List<int> Vrg_ChildKinds(VoorheesNode container)
    {
        List<int> lstKinds;                              // the kinds

        lstKinds = new List<int>();
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top level: keys. */
            lstKinds.Add(VRG_KEY);
            lstKinds.Add(VRG_DELETEKEY);
        }
        else if (container.Vn_iKind == VRG_KEY)
        {   /* A key: values, regedit's own order. */
            lstKinds.Add(VRG_SZ);
            lstKinds.Add(VRG_BINARY);
            lstKinds.Add(VRG_DWORD);
            lstKinds.Add(VRG_QWORD);
            lstKinds.Add(VRG_MULTISZ);
            lstKinds.Add(VRG_EXPANDSZ);
            lstKinds.Add(VRG_DELETEVALUE);
        }

        /* The kinds that fit (none under a key being deleted). */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_PanelKinds:                                                        */
    /*                                                                        */
    /* What the Type box offers: a key is a Key or a Delete Key; a value any  */
    /* value kind (Other Hex only for a value that is one).                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the entry's value.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds (a new list).                                */
    /*------------------------------------------------------------------------*/
    public static List<int> Vrg_PanelKinds(VoorheesNode node)
    {
        List<int> lstKinds;                              // the kinds

        lstKinds = new List<int>();
        if (node.Vn_IsContainer())
        {   /* A key. */
            lstKinds.Add(VRG_KEY);
            lstKinds.Add(VRG_DELETEKEY);
            return(lstKinds);
        }

        lstKinds.Add(VRG_SZ);
        lstKinds.Add(VRG_BINARY);
        lstKinds.Add(VRG_DWORD);
        lstKinds.Add(VRG_QWORD);
        lstKinds.Add(VRG_MULTISZ);
        lstKinds.Add(VRG_EXPANDSZ);
        lstKinds.Add(VRG_DELETEVALUE);
        if (node.Vn_iKind == VRG_HEXOTHER)
        {   /* Its own kind too. */
            lstKinds.Add(VRG_HEXOTHER);
        }

        /* The value kinds. */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ValueFromPanel:                                                    */
    /*                                                                        */
    /* The value the edit panel describes: a value of the kind chosen from    */
    /* the Value box (unchanged kind and text keep it exactly as written), or */
    /* a key becoming a key to delete (only when it holds no values) or back. */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind   : the VRG_ kind chosen.                                    */
    /*     sText   : the Value box's text.                                    */
    /*     oldNode : the value being replaced.                                */
    /*     sError  : set to why the value is refused, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null when refused.                    */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vrg_ValueFromPanel(int iKind, string sText,
        VoorheesNode oldNode, out string sError)
    {
        VoorheesNode container;                          // a key of the other kind
        int i;

        sError = null;
        if (oldNode.Vn_IsContainer())
        {   /* A key. */
            if (iKind == oldNode.Vn_iKind)
            {   /* Unchanged. */
                return(oldNode);
            }
            if (iKind != VRG_KEY && iKind != VRG_DELETEKEY)
            {   /* A key can't become a value. */
                sError = "A key can't become a value.";
                return(null);
            }
            for (i = 0; i < oldNode.Vn_Count(); i++)
            {
                if (iKind == VRG_DELETEKEY && oldNode.Vn_lstItems[i].Vi_IsValue())
                {   /* Values would go nowhere. */
                    sError = "A key to delete holds no values: delete its values "
                        + "first.";
                    return(null);
                }
            }
            container = VoorheesNode.Vn_NewContainer(iKind);
            container.Vn_sClose = oldNode.Vn_sClose;
            container.Vn_lstItems.AddRange(oldNode.Vn_lstItems);
            return(container);
        }

        if (iKind == VRG_KEY || iKind == VRG_DELETEKEY)
        {   /* A value can't become a key. */
            sError = "A value can't become a key; add a key at the top level.";
            return(null);
        }

        if (iKind == oldNode.Vn_iKind && sText.Replace("\r\n", "\n")
            .Replace('\r', '\n') == oldNode.Vn_sText)
        {   /* The same value: exactly as it was written. */
            return(oldNode);
        }

        /* A value of the kind, or refused with the reason. */
        return(Vrg_MakeValue(iKind, sText, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NewValue:                                                          */
    /*                                                                        */
    /* An empty value of a kind: "" for the strings and binary, 0 for the     */
    /* numbers, an empty key.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VRG_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value.                                          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vrg_NewValue(int iKind)
    {
        VoorheesNode container;                          // a new key

        if (iKind == VRG_KEY || iKind == VRG_DELETEKEY)
        {   /* An empty key. */
            container = VoorheesNode.Vn_NewContainer(iKind);
            container.Vn_sClose = "";
            return(container);
        }

        if (iKind == VRG_DWORD)
        {   /* Zero. */
            return(VoorheesNode.Vn_NewLeaf(iKind, "0x00000000"));
        }

        if (iKind == VRG_QWORD)
        {   /* Zero. */
            return(VoorheesNode.Vn_NewLeaf(iKind, "0x0000000000000000"));
        }

        if (iKind == VRG_HEXOTHER)
        {   /* No bytes of type 0. */
            return(VoorheesNode.Vn_NewLeaf(iKind, "hex(0):"));
        }

        /* Empty. */
        return(VoorheesNode.Vn_NewLeaf(iKind, ""));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ConvertText:                                                       */
    /*                                                                        */
    /* A value of the same kind from edited text (in-place editing, Replace): */
    /* the same text keeps it as written; other text must be valid for the    */
    /* kind; keys and values to delete have no text.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     oldNode : the value being replaced.                                */
    /*     sText   : the new text.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value, or null when refused.                */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vrg_ConvertText(VoorheesNode oldNode, string sText)
    {
        string sError;                                   // why refused (not reported here)

        if (oldNode.Vn_IsContainer() || oldNode.Vn_iKind == VRG_DELETEVALUE)
        {   /* No text to change. */
            return(null);
        }

        if (sText.Replace("\r\n", "\n").Replace('\r', '\n') == oldNode.Vn_sText)
        {   /* Unchanged. */
            return(oldNode);
        }

        /* A valid value of its kind, or null. */
        return(Vrg_MakeValue(oldNode.Vn_iKind, sText, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NewChild:                                                          */
    /*                                                                        */
    /* Add Child's new item: a key under HKEY_CURRENT_USER\Software with a    */
    /* free name, or a value named with the first free variant of             */
    /* "NewValue".                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (for free names).                         */
    /*     container : the container.                                         */
    /*     iKind     : a VRG_ kind.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vrg_NewChild(VoorheesDocument doc,
        VoorheesNode container, int iKind)
    {
        /* A free name for the kind. */
        return(VoorheesItem.Vi_NewMember(doc.Vd_UnusedKey(container,
            Vrg_NewKeyBase(container)), Vrg_NewValue(iKind)));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NewKeyBase:                                                        */
    /*                                                                        */
    /* The name a new item starts from: a key path at the top level, else     */
    /* "NewValue".                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the base name.                                            */
    /*------------------------------------------------------------------------*/
    public static string Vrg_NewKeyBase(VoorheesNode container)
    {
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* A key. */
            return(VRG_NEWKEYBASE);
        }

        /* A value. */
        return(VRG_NEWVALUEBASE);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NameQuestion:                                                      */
    /*                                                                        */
    /* What Add asks before it adds a key (or a key to delete): the hive,     */
    /* chosen from a drop-down of the five full hive names (VRG_HIVES), and   */
    /* the                                                                    */
    /* path under it, starting at a free name -- HKEY_CURRENT_USER and        */
    /* "Software\NewKey" (or NewKey1, 2 ...: the first not in the file).      */
    /* Values ask nothing (a placeholder name, as before).                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc   : the document (for the free name).                          */
    /*     iKind : the kind being added.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNameQuestion : the question, or null for a value.          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNameQuestion Vrg_NameQuestion(VoorheesDocument doc, int iKind)
    {
        string sFree;                                    // a free key path under the default hive
        string sTitle;                                   // the dialog's title
        int iHive;                                       // the default hive's entry

        if (iKind != VRG_KEY && iKind != VRG_DELETEKEY)
        {   /* A value: a placeholder name will do. */
            return(null);
        }

        sTitle = "Add Key";
        if (iKind == VRG_DELETEKEY)
        {   /* A key the file deletes. */
            sTitle = "Add Key to Delete";
        }

        /* The default: the new-key base's hive, and the free path's rest. */
        sFree = doc.Vd_UnusedKey(doc.Vd_root, VRG_NEWKEYBASE);
        iHive = Vrg_HiveIndex(sFree);
        return(VoorheesNameQuestion.Vnq_CreateChoice(sTitle, "Hive:", VRG_HIVES, iHive,
            "Key path under the hive:", sFree.Substring(VRG_HIVES[iHive].Length + 1)));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_RenameQuestion:                                                    */
    /*                                                                        */
    /* What Rename Key asks for a key (or a key to delete): the same hive     */
    /* drop-down and path box as adding one (Vrg_NameQuestion; the user,      */
    /* 2026-10-07: "go ahead and add it to rename"), starting at the key as   */
    /* it is -- its hive chosen, the rest of its path in the box.  A key not  */
    /* under any hive (a file regedit would refuse) starts at                 */
    /* HKEY_CURRENT_USER with its whole path in the box.  A value's name asks */
    /* nothing special (the plain box).                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the entry being renamed.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNameQuestion : the question, or null for a value.          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNameQuestion Vrg_RenameQuestion(VoorheesItem item)
    {
        string sPath;                                    // the key's path under its hive
        string sTitle;                                   // the dialog's title
        int iHive;                                       // its hive's entry

        if (item.Vi_iKind != VoorheesItem.VI_MEMBER || !item.Vi_node.Vn_IsContainer())
        {   /* A value (or a comment): the plain box. */
            return(null);
        }

        sTitle = "Rename Key";
        if (item.Vi_node.Vn_iKind == VRG_DELETEKEY)
        {   /* A key the file deletes. */
            sTitle = "Rename Key to Delete";
        }

        iHive = Vrg_HiveIndex(item.Vi_sKey);
        if (iHive >= 0)
        {   /* Under a hive: the rest of the path (nothing for the hive itself). */
            sPath = "";
            if (item.Vi_sKey.Length > VRG_HIVES[iHive].Length)
            {   /* Something below it, after the backslash. */
                sPath = item.Vi_sKey.Substring(VRG_HIVES[iHive].Length + 1);
            }
        }
        else
        {   /* Not under a hive: the new-key base's hive, the whole path to fix. */
            iHive = Vrg_HiveIndex(VRG_NEWKEYBASE);
            sPath = item.Vi_sKey;
        }

        /* The key as it is. */
        return(VoorheesNameQuestion.Vnq_CreateChoice(sTitle, "Hive:", VRG_HIVES, iHive,
            "Key path under the hive:", sPath));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NameFromAnswer:                                                    */
    /*                                                                        */
    /* A key path from the hive chosen and the path typed under it: joined    */
    /* with one "\" (backslashes at either end of the typed path dropped; no  */
    /* path gives the hive itself).  A typed path that already starts with a  */
    /* hive (a full path pasted in) is taken as it is.  Anything else wrong   */
    /* with it (a "]", a line break) is refused when the name is given        */
    /* (Vrg_KeyProblem).                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sHive : the hive's full name.                                      */
    /*     sPath : the path typed.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the key path.                                             */
    /*------------------------------------------------------------------------*/
    public static string Vrg_NameFromAnswer(string sHive, string sPath)
    {
        string sRest;                                    // the typed path without outer backslashes

        if (Vrg_HasHive(sPath))
        {   /* A full path already: as typed. */
            return(sPath);
        }

        sRest = sPath.Trim('\\');
        if (sRest.Length == 0)
        {   /* Nothing under it: the hive itself. */
            return(sHive);
        }

        /* Hive, separator, path. */
        return(sHive + "\\" + sRest);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_HiveIndex:                                                         */
    /*                                                                        */
    /* Which of VRG_HIVES a key path starts with.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath : the path.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : its entry in VRG_HIVES, or -1 for none (compared without     */
    /*           case; the hive must be followed by nothing or "\").          */
    /*------------------------------------------------------------------------*/
    static int Vrg_HiveIndex(string sPath)
    {
        int i;

        for (i = 0; i < VRG_HIVES.Length; i++)
        {
            if (sPath.StartsWith(VRG_HIVES[i], StringComparison.OrdinalIgnoreCase)
                && (sPath.Length == VRG_HIVES[i].Length
                || sPath[VRG_HIVES[i].Length] == '\\'))
            {   /* This hive. */
                return(i);
            }
        }

        /* None. */
        return(-1);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_FitItem:                                                           */
    /*                                                                        */
    /* Whether an item can go into a container: comments anywhere; keys at    */
    /* the top level only (a flat list, decision R1); values in a key, not    */
    /* under a key to delete; every entry needs a name.                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : where it goes.                                         */
    /*     item      : the item.                                              */
    /*     sError    : set to why it cannot go there, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item, or null when refused.                     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vrg_FitItem(VoorheesNode container,
        VoorheesItem item, out string sError)
    {
        sError = null;
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* Comments go anywhere. */
            return(item);
        }

        if (item.Vi_iKind == VoorheesItem.VI_ELEMENT)
        {   /* No name. */
            sError = "Registry values need a name.";
            return(null);
        }

        if (item.Vi_node.Vn_IsContainer() && container.Vn_iKind != VoorheesNode.VN_DOCUMENT)
        {   /* A key inside a key. */
            sError = "Keys are listed at the top level, each with its full path.";
            return(null);
        }

        if (!item.Vi_node.Vn_IsContainer() && container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* A value outside any key. */
            sError = "A value must be inside a key.";
            return(null);
        }

        if (!item.Vi_node.Vn_IsContainer() && container.Vn_iKind == VRG_DELETEKEY)
        {   /* A value under a key to delete. */
            sError = "A key being deleted holds no values.";
            return(null);
        }

        /* It fits. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_KeyProblem:                                                        */
    /*                                                                        */
    /* Why a new name would not do: one line; a key path starts with a full   */
    /* hive name (HKEY_...) and holds no "]".  A value name may be anything   */
    /* on one line ("" makes it the default value, "@").                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item    : the entry (its value says what it is).                   */
    /*     sNewKey : the proposed name.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is acceptable.                */
    /*------------------------------------------------------------------------*/
    public static string Vrg_KeyProblem(VoorheesItem item, string sNewKey)
    {
        if (sNewKey.IndexOf('\r') >= 0 || sNewKey.IndexOf('\n') >= 0)
        {   /* A line break. */
            return("A name is one line.");
        }

        if (!item.Vi_node.Vn_IsContainer())
        {   /* A value name: anything else goes. */
            return(null);
        }

        if (sNewKey.IndexOf(']') >= 0)
        {   /* Would end the key line early. */
            return("A key path can't contain \"]\".");
        }

        if (!Vrg_HasHive(sNewKey))
        {   /* Not under a hive. */
            return("A key path starts with a full hive name: "
                + string.Join(", ", VRG_HIVES) + ".");
        }

        /* Acceptable. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_HasHive:                                                           */
    /*                                                                        */
    /* Whether a key path starts with a full hive name (then nothing or "\"). */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath : the path.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it does (compared without case).                  */
    /*------------------------------------------------------------------------*/
    static bool Vrg_HasHive(string sPath)
    {
        /* One of the hives, found by Vrg_HiveIndex. */
        return(Vrg_HiveIndex(sPath) >= 0);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_CountBadHives:                                                     */
    /*                                                                        */
    /* How many keys do not start with a full hive name (regedit refuses to   */
    /* import them), for the open warning.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root : the document node.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the count.                                                   */
    /*------------------------------------------------------------------------*/
    static int Vrg_CountBadHives(VoorheesNode root)
    {
        int iCount;                                      // keys found so far
        int i;

        iCount = 0;
        for (i = 0; i < root.Vn_Count(); i++)
        {
            if (root.Vn_lstItems[i].Vi_IsValue()
                && root.Vn_lstItems[i].Vi_node.Vn_IsContainer()
                && !Vrg_HasHive(root.Vn_lstItems[i].Vi_sKey))
            {   /* Not under a hive. */
                iCount++;
            }
        }

        /* Every one. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_OpenWarnings:                                                      */
    /*                                                                        */
    /* What the user should be told when a registry file opens: repeated keys */
    /* or values (the last copy wins on import), keys not under a hive.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document.                                       */
    /*     sFileName    : its file's name (no folder).                        */
    /*     iExtraCopies : repeated-name copies (compared without case).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the messages (empty for none).                      */
    /*------------------------------------------------------------------------*/
    public static List<string> Vrg_OpenWarnings(VoorheesDocument doc,
        string sFileName, int iExtraCopies)
    {
        List<string> lstWarnings;                        // the messages
        int iBad;                                        // keys not under a hive

        lstWarnings = new List<string>();
        if (iExtraCopies > 0)
        {   /* Something repeats. */
            lstWarnings.Add(sFileName + " repeats a key or a value ("
                + iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " extra copies or more; names are compared without case)."
                + "\r\n\r\nWhen the file is imported the LAST copy wins.  Voorhees "
                + "keeps every copy, marks them \"(duplicate)\" in the tree and "
                + "saves them all back.");
        }

        iBad = Vrg_CountBadHives(doc.Vd_root);
        if (iBad > 0)
        {   /* regedit would refuse those keys. */
            lstWarnings.Add(iBad.ToString(CultureInfo.InvariantCulture)
                + " key(s) in " + sFileName + " do not start with a full hive "
                + "name (HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER ...); regedit will "
                + "not import them.");
        }

        /* Every warning, or none. */
        return(lstWarnings);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ValidateWarnings:                                                  */
    /*                                                                        */
    /* --validate's one-line warnings: repeats, keys not under a hive.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document.                                       */
    /*     iExtraCopies : repeated-name copies.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the warnings (empty for none).                      */
    /*------------------------------------------------------------------------*/
    public static List<string> Vrg_ValidateWarnings(VoorheesDocument doc,
        int iExtraCopies)
    {
        List<string> lstWarnings;                        // the warnings
        int iBad;                                        // keys not under a hive

        lstWarnings = new List<string>();
        if (iExtraCopies > 0)
        {   /* Repeats. */
            lstWarnings.Add(iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " repeated key(s) or value(s) (compared without case); the last "
                + "copy wins on import");
        }

        iBad = Vrg_CountBadHives(doc.Vd_root);
        if (iBad > 0)
        {   /* Not importable. */
            lstWarnings.Add(iBad.ToString(CultureInfo.InvariantCulture)
                + " key(s) not under a hive; regedit will not import them");
        }

        /* Every warning, or none. */
        return(lstWarnings);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_LabelValue:                                                        */
    /*                                                                        */
    /* A value as its tree label shows it: numbers "0x0000001f (31)", a value */
    /* to delete "(delete)", anything else its text.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the value.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the label's value part.                                   */
    /*------------------------------------------------------------------------*/
    public static string Vrg_LabelValue(VoorheesNode node)
    {
        if (node.Vn_iKind == VRG_DWORD || node.Vn_iKind == VRG_QWORD)
        {   /* Hex and decimal. */
            return(node.Vn_sText + " (" + Vrg_ParseNumber(node.Vn_sText,
                node.Vn_iKind == VRG_QWORD).ToString(CultureInfo.InvariantCulture)
                + ")");
        }

        if (node.Vn_iKind == VRG_DELETEVALUE)
        {   /* Nothing to show but what it does. */
            return("(delete)");
        }

        /* The text. */
        return(node.Vn_sText);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ItemName:                                                          */
    /*                                                                        */
    /* An entry's name in the tree: the default value "(Default)", anything   */
    /* else its name or path.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the entry.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name.                                                 */
    /*------------------------------------------------------------------------*/
    public static string Vrg_ItemName(VoorheesItem item)
    {
        if (item.Vi_iKind != VoorheesItem.VI_MEMBER)
        {   /* Unkeyed (never in a registry file). */
            return("");
        }

        if (item.Vi_sKey.Length == 0 && !item.Vi_node.Vn_IsContainer())
        {   /* "@". */
            return("(Default)");
        }

        /* Its name. */
        return(item.Vi_sKey);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_WriteHeader:                                                       */
    /*                                                                        */
    /* A key's line: "[" (and "-" for a key to delete), the path, "]", then   */
    /* whatever followed it as written.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the key.                                                    */
    /*     sb   : where the line goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended.                                       */
    /*------------------------------------------------------------------------*/
    public static void Vrg_WriteHeader(VoorheesItem item, StringBuilder sb)
    {
        sb.Append('[');
        if (item.Vi_node.Vn_iKind == VRG_DELETEKEY)
        {   /* Delete it. */
            sb.Append('-');
        }
        sb.Append(item.Vi_sKey);
        sb.Append(']');
        if (item.Vi_sValueGap != null)
        {   /* As written. */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ResetLayout:                                                       */
    /*                                                                        */
    /* Clears a copied item's layout (Paste): lead, gaps, the quoted name and */
    /* the raw value (regenerated in regedit's form), inside a key too.       */
    /* ONLY for a copy not in a document.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the copy.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the layout fields are null.                                 */
    /*------------------------------------------------------------------------*/
    public static void Vrg_ResetLayout(VoorheesItem item)
    {
        int i;

        item.Vi_sLead = null;
        item.Vi_sKeyGap = null;
        item.Vi_sValueGap = null;
        if (item.Vi_node == null)
        {   /* A comment. */
            return;
        }

        if (!item.Vi_node.Vn_IsContainer())
        {   /* A value: name and value generated afresh. */
            item.Vi_sKeyRaw = null;
            item.Vi_node.Vn_sRaw = null;
            return;
        }

        /* A key: its values too. */
        item.Vi_node.Vn_sClose = "";
        for (i = 0; i < item.Vi_node.Vn_lstItems.Count; i++)
        {
            Vrg_ResetLayout(item.Vi_node.Vn_lstItems[i]);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_DescribeStyle:                                                     */
    /*                                                                        */
    /* --validate --verbose's style summary: the header.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "regedit 5".                                         */
    /*------------------------------------------------------------------------*/
    public static string Vrg_DescribeStyle(VoorheesDocument doc)
    {
        if (Vrg_IsAnsi(doc.Vd_root))
        {   /* The old form. */
            return("REGEDIT4 (ANSI strings)");
        }

        /* The current form. */
        return("regedit 5");
    }
}
