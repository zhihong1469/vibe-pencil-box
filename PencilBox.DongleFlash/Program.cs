// <copyright file="Program.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.DongleFlash;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 单实例：同一时刻只允许一个烧录进程（01 号 §2.3-5）
        using var mutex = new Mutex(initiallyOwned: true, @"Local\PencilBox.DongleFlash", out var createdNew);
        if (!createdNew)
        {
            System.Windows.Forms.MessageBox.Show(
                "R1D 产线烧录已在运行（同一时刻只允许一个烧录实例）。",
                "R1D 产线烧录", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new MainForm());
    }
}
