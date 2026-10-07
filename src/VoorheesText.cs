/*----------------------------------------------------------------------------*/
/* VoorheesText.cs                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* Text in and out of files, for every format: file bytes to text and back    */
/* (byte order mark, strict UTF-8, ANSI and ISO-8859-1 fall-backs that        */
/* reproduce the bytes exactly) and the line-ending style a file uses.        */
/* Nothing here knows any file format: the readers and writers of each        */
/* format (VoorheesJson.cs and the others named in VoorheesFormat.cs) turn    */
/* the text into the model and back.                                          */
/*                                                                            */
/* Class:                                                                     */
/*     VoorheesText (Vtx_) : static functions only; nothing is kept between   */
/*                           calls, so any of them may run on a worker        */
/*                           thread.                                          */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*----------------------------------------------------------------------------*/
/* VoorheesText                                                               */
/*                                                                            */
/* Bytes to text and back, and line-ending detection, shared by every file    */
/* format.                                                                    */
/*----------------------------------------------------------------------------*/
static class VoorheesText
{
    /*------------------------------------------------------------------------*/
    /* Code pages that have a byte order mark, and the one-to-one fall-back.  */
    /*------------------------------------------------------------------------*/

    const int VTX_CP_UTF8 = 65001;                       // code page: UTF-8
    const int VTX_CP_UTF16LE = 1200;                     // code page: UTF-16 little-endian
    const int VTX_CP_UTF16BE = 1201;                     // code page: UTF-16 big-endian
    const int VTX_CP_UTF32LE = 12000;                    // code page: UTF-32 little-endian
    const int VTX_CP_LATIN1 = 28591;                     // code page: ISO-8859-1, which maps every byte to one character and back

    /*------------------------------------------------------------------------*/
    /* Vtx_Decode:                                                            */
    /*                                                                        */
    /* Turns a file's bytes into text, so that Vtx_Encode can turn the text   */
    /* back into the SAME bytes:                                              */
    /*     o a byte order mark decides the encoding (UTF-8, UTF-16 LE or BE,  */
    /*       UTF-32 LE) and is remembered, not kept in the text;              */
    /*     o otherwise the bytes are decoded as UTF-8 with a decoder that     */
    /*       THROWS on invalid input, so a file that is not UTF-8 is noticed  */
    /*       rather than silently damaged;                                    */
    /*     o such a file is decoded with the Windows ANSI code page, which is */
    /*       what it almost certainly is, provided the bytes come back the    */
    /*       same; if they would not (a byte the code page has no character   */
    /*       for), ISO-8859-1 is used, which maps every byte one to one.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes    : the file.                                               */
    /*     encoding : set to the encoding to write it back with.              */
    /*     bBom     : set to true when the file started with a byte order     */
    /*                mark.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the decoded text, without the byte order mark.            */
    /*------------------------------------------------------------------------*/
    public static string Vtx_Decode(byte[] bytes, out Encoding encoding,
        out bool bBom)
    {
        Encoding strict;                                 // UTF-8 that throws on invalid bytes
        Encoding ansi;                                   // the Windows ANSI code page
        string sText;                                    // the decoded text
        byte[] check;                                    // the text encoded again, to compare

        bBom = false;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB
            && bytes[2] == 0xBF)
        {   /* UTF-8 byte order mark. */
            bBom = true;
            encoding = new UTF8Encoding(false);
            return(encoding.GetString(bytes, 3, bytes.Length - 3));
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE
            && bytes[2] == 0x00 && bytes[3] == 0x00)
        {   /* UTF-32 little-endian byte order mark (checked before UTF-16, which starts the same). */
            bBom = true;
            encoding = new UTF32Encoding(false, false);
            return(encoding.GetString(bytes, 4, bytes.Length - 4));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {   /* UTF-16 little-endian byte order mark. */
            bBom = true;
            encoding = new UnicodeEncoding(false, false);
            return(encoding.GetString(bytes, 2, bytes.Length - 2));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {   /* UTF-16 big-endian byte order mark. */
            bBom = true;
            encoding = new UnicodeEncoding(true, false);
            return(encoding.GetString(bytes, 2, bytes.Length - 2));
        }

        /* No byte order mark: UTF-8 if the bytes are valid UTF-8. */
        strict = new UTF8Encoding(false, true);
        try
        {
            sText = strict.GetString(bytes);
            encoding = new UTF8Encoding(false);
            return(sText);
        }
        catch (DecoderFallbackException)
        {   /* Not UTF-8: fall through to the ANSI code page. */
        }

        ansi = Encoding.Default;
        sText = ansi.GetString(bytes);
        check = ansi.GetBytes(sText);
        if (Vtx_SameBytes(check, bytes))
        {   /* The ANSI code page reproduces the file exactly: use it. */
            encoding = ansi;
            return(sText);
        }

        /* Bytes the ANSI code page cannot hold: ISO-8859-1 always can. */
        encoding = Encoding.GetEncoding(VTX_CP_LATIN1);
        return(encoding.GetString(bytes));
    }

    /*------------------------------------------------------------------------*/
    /* Vtx_SameBytes:                                                         */
    /*                                                                        */
    /* Whether two byte arrays are identical.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     a : one array.                                                     */
    /*     b : the other.                                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when both have the same length and contents.           */
    /*------------------------------------------------------------------------*/
    public static bool Vtx_SameBytes(byte[] a, byte[] b)
    {
        int i;

        if (a.Length != b.Length)
        {   /* Different lengths cannot match. */
            return(false);
        }

        for (i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {   /* First difference. */
                return(false);
            }
        }

        /* Every byte equal. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* Vtx_Encode:                                                            */
    /*                                                                        */
    /* Turns text back into file bytes: the byte order mark first if the      */
    /* file had one, then the text in its encoding.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText    : the text.                                               */
    /*     encoding : the encoding (from Vtx_Decode or the document).         */
    /*     bBom     : write a byte order mark first.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the bytes to write.                                       */
    /*------------------------------------------------------------------------*/
    public static byte[] Vtx_Encode(string sText, Encoding encoding, bool bBom)
    {
        byte[] bom;                                      // the byte order mark, or none
        byte[] body;                                     // the encoded text
        byte[] all;                                      // both together

        bom = new byte[0];
        if (bBom)
        {   /* The mark for this encoding. */
            bom = Vtx_BomFor(encoding);
        }

        body = encoding.GetBytes(sText);
        all = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, all, 0, bom.Length);
        Buffer.BlockCopy(body, 0, all, bom.Length, body.Length);

        /* Mark, then text. */
        return(all);
    }

    /*------------------------------------------------------------------------*/
    /* Vtx_BomFor:                                                            */
    /*                                                                        */
    /* The byte order mark of an encoding.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     encoding : the encoding.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : its mark; empty for an encoding that has none (an ANSI    */
    /*              code page).                                               */
    /*------------------------------------------------------------------------*/
    static byte[] Vtx_BomFor(Encoding encoding)
    {
        switch (encoding.CodePage)
        {
            case VTX_CP_UTF8:    return(new byte[] { 0xEF, 0xBB, 0xBF });          // UTF-8
            case VTX_CP_UTF16LE: return(new byte[] { 0xFF, 0xFE });                // UTF-16 LE
            case VTX_CP_UTF16BE: return(new byte[] { 0xFE, 0xFF });                // UTF-16 BE
            case VTX_CP_UTF32LE: return(new byte[] { 0xFF, 0xFE, 0x00, 0x00 });    // UTF-32 LE
            default:             return(new byte[0]);                              // code pages have no mark
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtx_DetectNewline:                                                     */
    /*                                                                        */
    /* The line ending a file uses, for generated line breaks: the one its    */
    /* first line break uses.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the decoded file.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : "\r\n" or "\n"; null when the file has no line break, so  */
    /*              the document takes its default.                           */
    /*------------------------------------------------------------------------*/
    public static string Vtx_DetectNewline(string sText)
    {
        int iFirst;                                      // the first line feed

        iFirst = sText.IndexOf('\n');
        if (iFirst < 0)
        {   /* No line breaks to go by. */
            return(null);
        }

        if (iFirst > 0 && sText[iFirst - 1] == '\r')
        {   /* Windows style. */
            return("\r\n");
        }

        /* Unix style. */
        return("\n");
    }
}
