/*--------------------------------------------------------------------------*/
/* Version.cs                                                               */
/*                                                                          */
/* SPDX-License-Identifier: GPL-2.0-or-later                                */
/* Copyright (c) 2026 B. C. Services                                        */
/*                                                                          */
/*--------------------------------------------------------------------------*/
/* The Voorhees version number, shown in the About box and by --version.    */
/*                                                                          */
/* GENERATED: compile.bat rewrites this whole file each time it runs,       */
/* adding one to the last number first, so any edit here is overwritten.    */
/* To move to a new major or minor version, change the first two numbers    */
/* here and set the last one to -1, so the next build is x.y.0.             */
/*--------------------------------------------------------------------------*/

static class VoorheesVersion
{
    /* major.minor.build */
    public const string V = "2.5.0";
}
