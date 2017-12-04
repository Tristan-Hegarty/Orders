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
    public interface IOrderBook
    {
        void ParseNewInfo(JToken item);
        void CancelWritingOutputFile();
        bool FileWriterFinished { get; }
    }

    public class OrderBook : IOrderBook
    {
        public bool FileWriterFinished { get; private set; }

        // create a backgoundWorker to write complete orders to the output files
        private BackgroundWorker m_background_worker = new BackgroundWorker();
        // (thread-safe) dictionary <unique order string, order>
        private ConcurrentDictionary<string, Order> m_orders = new ConcurrentDictionary<string, Order>();
        // full list of properties that comprise an order
        private string[] m_order_properties = new string[]
        {
            "order reference",
            "marketplace",
            "name",
            "surname",
            "order item number",
            "sku",
            "price per unit",
            "quantity",
            "postal service",
            "postcode"
        };

        public OrderBook(string output_dir, int periodicity_ms)
        {
            FileWriterFinished = false;

            m_background_worker.DoWork += backgroundWorker_DoWork;
            m_background_worker.RunWorkerCompleted += backgroundWorker_RunWorkerCompleted;
            m_background_worker.WorkerSupportsCancellation = true;
            // make worker thread create new output files at same periodicity as the input file scanner interval
            m_background_worker.RunWorkerAsync(new object[] { output_dir, periodicity_ms });
        }

        public void CancelWritingOutputFile()
        {
            m_background_worker.CancelAsync();
        }

        public void ParseNewInfo(JToken item)
        {
            if (item.Children<JProperty>().Count() == 0)
                return;

            string order_ref = "";
            string marketplace = "";
            string order_item_num = "";

            if (!FindElement(item, "order reference", out order_ref) ||
                !FindElement(item, "marketplace", out marketplace))
                return;

            // concatenate 'order reference' + 'marketplace',
            // 'cos together these should provide a unique string
            string unique_order = marketplace + "-" + order_ref;
            m_orders.TryAdd(unique_order, new Order());

            // check if this is an 'order item'
            if (FindElement(item, "order item number", out order_item_num))
            {
                m_orders[unique_order].items.TryAdd(order_item_num, new OrderItem());
            }
            else if (order_item_num == "null")
                // can not process an order item without the order_item_num
                return;

            foreach (var element in item.Children<JProperty>())
            {
                if (m_order_properties.Contains(element.Name) && element.HasValues)
                {
                    switch (element.Name)
                    {
                        case "order reference":
                            m_orders[unique_order].order_ref = Convert.ToString(element.Value);
                            break;

                        case "marketplace":
                            m_orders[unique_order].marketplace = Convert.ToString(element.Value);
                            break;

                        case "name":
                            m_orders[unique_order].name = Convert.ToString(element.Value);
                            break;

                        case "surname":
                            m_orders[unique_order].surname = Convert.ToString(element.Value);
                            break;

                        case "order item number":   // already added as 'Key' item for Order.items
                            break;

                        case "sku":
                            m_orders[unique_order].items[order_item_num].sku = Convert.ToString(element.Value);
                            break;

                        case "price per unit":
                            m_orders[unique_order].items[order_item_num].price = Convert.ToDouble(element.Value);
                            break;

                        case "quantity":
                            m_orders[unique_order].items[order_item_num].quantity = Convert.ToInt32(element.Value);
                            break;

                        case "postal service":
                            m_orders[unique_order].postal_service = Convert.ToString(element.Value);
                            break;

                        case "postcode":
                            m_orders[unique_order].postcode = Convert.ToString(element.Value);
                            break;
                    }
                }
            }
        }

        private void backgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            string output_dir = Convert.ToString(((object[])e.Argument)[0]);
            int periodicity_ms = Convert.ToInt32(((object[])e.Argument)[1]);

            if (periodicity_ms == 0)
                periodicity_ms = 10000; // 10 seconds

            Stopwatch sw = new Stopwatch();

            while (!m_background_worker.CancellationPending)
            {
                if (!backgroundWorkerSleep(periodicity_ms - (int)sw.ElapsedMilliseconds))
                    // then user has quit program during sleep
                    return;

                if (!Directory.Exists(output_dir))
                    continue;

                sw.Restart();
                foreach (KeyValuePair<string, Order> order in m_orders)
                {
                    StringBuilder sb = new StringBuilder();
                    List<string> order_item_nums;
                    if (order.Value.NewOrdersReady(out order_item_nums))
                    {
                        foreach (string item_num in order_item_nums)
                        {
                            OrderItem order_item = order.Value.items[item_num];

                            sb.AppendLine(
                                string.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}",
                                order.Value.order_ref,
                                order.Value.marketplace,
                                order.Value.name,
                                order.Value.surname,
                                item_num,
                                order_item.sku,
                                order_item.price,
                                order_item.quantity,
                                order.Value.postal_service,
                                order.Value.postcode));
                        }

                        string filepath = output_dir + "\\" + order.Key + ".csv";
                        string headers = "";
                        if (!File.Exists(filepath))
                        {
                            headers = "";
                            foreach (string column in m_order_properties)
                                headers += column + ",";

                            File.AppendAllText(filepath, headers + Environment.NewLine);
                        }
                        if (OrdersAddedToFile(filepath, sb))
                        {
                            // remove the order items so that they are not continuously checked in future
                            foreach (string item_num in order_item_nums)
                            {
                                OrderItem order_item;
                                order.Value.items.TryRemove(item_num, out order_item);
                            }
                        }
                        
                    }
                }
                sw.Stop();
            }
        }

        private bool OrdersAddedToFile(string filepath, StringBuilder sb)
        {
            // check if the file is available for writing
            try
            {
                using (var stream = new FileStream(filepath, FileMode.Open, FileAccess.Read))
                { }
            }
            catch (IOException)
            {
                return false;
            }
            File.AppendAllText(filepath, sb.ToString() + Environment.NewLine);
            return true;
        }

        private bool backgroundWorkerSleep(int interval_ms)
        {
            if (interval_ms > 0)
            {
                if (interval_ms > 1000)
                {
                    int seconds = interval_ms / 1000;
                    for (int i = 0; i < seconds; i++)
                    {
                        if (m_background_worker.CancellationPending)
                            return false;

                        Thread.Sleep(1000);
                    }
                }
                else
                    Thread.Sleep(interval_ms);
            }
            return true;
        }

        private void backgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            FileWriterFinished = true;
        }

        private bool FindElement(JToken item, string type, out string out_value)
        {
            out_value = "";
            foreach (var element in item.Children<JProperty>())
            {
                if (element.Name == type)
                {
                    if (element.HasValues && Convert.ToString(element.Value).Length > 0)
                    {
                        out_value = Convert.ToString(element.Value);
                        return true;
                    }
                    else
                        out_value = "null";
                }
            }
            return false;
        }
    }

    public class Order
    {
        public string order_ref = "";
        public string marketplace = "";
        public string name = "";
        public string surname = "";
        public string postal_service = "";
        public string postcode = "";

        // (thread-safe) dictionary <order item num, order items>
        public ConcurrentDictionary<string, OrderItem> items = new ConcurrentDictionary<string, OrderItem>();

        public bool NewOrdersReady(out List<string> order_item_nums)
        {
            order_item_nums = new List<string>();

            bool maybe =
                    order_ref.Length > 0 &&
                    marketplace.Length > 0 &&
                    name.Length > 0 &&
                    surname.Length > 0 &&
                    postal_service.Length > 0 &&
                    postcode.Length > 0 &&
                    items.Count > 0;

            if (!maybe)
                return false;

            foreach (KeyValuePair<string, OrderItem> item in items)
            {
                if (item.Value.ItemReady)
                    order_item_nums.Add(item.Key);
            }
            return order_item_nums.Count > 0;
        }
    }

    public class OrderItem
    {
        public string sku = "";
        public double price = -1.0;
        public int quantity = 0;

        public bool ItemReady
        {

            get
            {
                return
                    sku.Length > 0 &&
                    price >= 0.0 &&
                    quantity > 0;
            }
        }
    }
}
