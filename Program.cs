using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CollateOrders
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "-?")
            {
                DisplayHelp();
                return;
            }

            // If either input/output directories are not specified, exit program
            if (args.Length != 2)
            {
                PrintErrorMessage("Expecting: CollateOrders.exe <input file directory> <output file directory>");
                return;
            }

            // scan the input directory every 30 seconds
            const int periodicity_ms = 30000;

            AutoResetEvent quit = new AutoResetEvent(false);
            IOrderBook order_book = new OrderBook(args[1], periodicity_ms);
            ArrayList old_files = new ArrayList();

            object state = new object[] { args, old_files, order_book, quit };

            Timer timer = new Timer(ScanFiles, state, 0, periodicity_ms);

            bool user_quits = false;

            while (!user_quits)
            {
                quit.WaitOne();

                // disable the timer
                timer.Change(Timeout.Infinite, Timeout.Infinite);
                Console.WriteLine("Press 'q' to quit program, any other key to continue...");

                if (Console.ReadKey(true).KeyChar == 'q')
                {
                    user_quits = true;
                    order_book.CancelWritingOutputFile();
                    Console.WriteLine("Quitting program. Just waiting for output files thread to stop");

                    while (!order_book.FileWriterFinished)
                    {
                        Console.Write('.');
                        Thread.Sleep(500);
                    }
                }
                else
                {
                    Console.WriteLine("Program continuing");
                    // re-enable the timer
                    timer.Change(0, periodicity_ms);
                }
            }

            timer.Dispose();
        }

        private static void DisplayHelp()
        {
            Console.WriteLine(
                "CollateOrders.exe takes two arguments:\n" +
                "<input file directory> and <output file directory>\n" +
                "<input file directory> is where new *.json files are periodically searched and orders information is extracted\n" +
                "<output file directory> is where files containing complete orders are created");
        }

        private static void PrintErrorMessage(string message)
        {
            if (message.Length > 0)
            {
                Console.WriteLine("Error: " + message);
            }
        }
        
        private static void ScanFiles(object state)
        {
            string[] dirs = (string[])((object[])state)[0];
            ArrayList old_files = (ArrayList)((object[])state)[1];
            IOrderBook order_book = (OrderBook)((object[])state)[2];
            AutoResetEvent quit = (AutoResetEvent)((object[])state)[3];

            // dirs[0] = input file directory
            if (!Directory.Exists(dirs[0]))
            {
                PrintErrorMessage("Can not locate input file directory: '" + dirs[0] + "'");
                quit.Set();
                return;
            }

            // dirs[1] = output file directory
            if (!Directory.Exists(dirs[1]))
            {
                PrintErrorMessage("Can not locate output file directory: '" + dirs[1] + "'");
                quit.Set();
                return;
            }

            // synchronise to make ArrayList thread-safe
            ArrayList sync_files = ArrayList.Synchronized(old_files);

            string[] input_files = Directory.GetFiles(dirs[0], "*.json", SearchOption.TopDirectoryOnly);

            List<string> new_files = new List<string>();

            // lock the sync_files list while we read newly found files
            lock (sync_files.SyncRoot)
            {
                foreach (string in_file in input_files)
                {
                    if (sync_files.Contains(in_file))
                        continue;

                    new_files.Add(in_file);
                    sync_files.Add(in_file);
                }
            }
            
            foreach (string file in new_files)
            {
                string file_text = File.ReadAllText(file);
                // ignore the first name that appears in file 'cos this is not necessarily unique for: orders, order_items, or order_shipments
                file_text = file_text.Substring(file_text.IndexOf('['));
                JArray jarray = JArray.Parse(file_text.Substring(0, file_text.LastIndexOf(']') + 1));

                foreach (var item in jarray.Children())
                {
                    order_book.ParseNewInfo(item);
                }
            }
        }
    }
}
