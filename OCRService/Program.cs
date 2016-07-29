/*
 * Provides a web service on port 8080 to which base-64 encoded png images can be POST for ocr.
 * 
 * Must run OneNote 2010 in the background, with a notebook with page name "ocr".
 * Set keyboard input language for the OneNote window to the language being OCR-ed.
 * 
 * Will set current machine's IP address at http://transgame.csail.mit.edu:9537/?varname=win7ipaddress
 * if not desired, uncomment that line.
 * 
 * Copyright Geza Kovacs <gkovacs@mit.edu>
 */

using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using System.Drawing;
using System.Drawing.Imaging;
using System.Collections;
using System.ComponentModel;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.IO;
using System.Data;
using OneNote = Microsoft.Office.Interop.OneNote;
using Microsoft.Office.Interop.OneNote;

namespace Bend.Util
{

    public class HttpProcessor
    {
        public TcpClient socket;
        public HttpServer srv;

        private Stream inputStream;
        public StreamWriter outputStream;

        public String http_method;
        public String http_url;
        public String http_protocol_versionstring;
        public Hashtable httpHeaders = new Hashtable();


        private static int MAX_POST_SIZE = 10 * 1024 * 1024; // 10MB

        public HttpProcessor(TcpClient s, HttpServer srv)
        {
            this.socket = s;
            this.srv = srv;
        }


        private string streamReadLine(Stream inputStream)
        {
            int next_char;
            string data = "";
            while (true)
            {
                next_char = inputStream.ReadByte();
                if (next_char == '\n') { break; }
                if (next_char == '\r') { continue; }
                if (next_char == -1) { Thread.Sleep(1); continue; };
                data += Convert.ToChar(next_char);
            }
            return data;
        }
        public void process()
        {
            // we can't use a StreamReader for input, because it buffers up extra data on us inside it's
            // "processed" view of the world, and we want the data raw after the headers
            inputStream = new BufferedStream(socket.GetStream());

            // we probably shouldn't be using a streamwriter for all output from handlers either
            outputStream = new StreamWriter(new BufferedStream(socket.GetStream()));
            try
            {
                parseRequest();
                readHeaders();
                if (http_method.Equals("GET"))
                {
                    handleGETRequest();
                }
                else if (http_method.Equals("POST"))
                {
                    handlePOSTRequest();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e.ToString());
                writeFailure();
            }
            outputStream.Flush();
            // bs.Flush(); // flush any remaining output
            inputStream = null; outputStream = null; // bs = null;            
            socket.Close();
        }

        public void parseRequest()
        {
            String request = streamReadLine(inputStream);
            string[] tokens = request.Split(' ');
            if (tokens.Length != 3)
            {
                throw new Exception("invalid http request line");
            }
            http_method = tokens[0].ToUpper();
            http_url = tokens[1];
            http_protocol_versionstring = tokens[2];

            Console.WriteLine("starting: " + request);
        }

        public void readHeaders()
        {
            Console.WriteLine("readHeaders()");
            String line;
            while ((line = streamReadLine(inputStream)) != null)
            {
                if (line.Equals(""))
                {
                    Console.WriteLine("got headers");
                    return;
                }

                int separator = line.IndexOf(':');
                if (separator == -1)
                {
                    throw new Exception("invalid http header line: " + line);
                }
                String name = line.Substring(0, separator);
                int pos = separator + 1;
                while ((pos < line.Length) && (line[pos] == ' '))
                {
                    pos++; // strip any spaces
                }

                string value = line.Substring(pos, line.Length - pos);
                Console.WriteLine("header: {0}:{1}", name, value);
                httpHeaders[name] = value;
            }
        }

        public void handleGETRequest()
        {
            srv.handleGETRequest(this);
        }

        private const int BUF_SIZE = 4096;
        public void handlePOSTRequest()
        {
            // this post data processing just reads everything into a memory stream.
            // this is fine for smallish things, but for large stuff we should really
            // hand an input stream to the request processor. However, the input stream 
            // we hand him needs to let him see the "end of the stream" at this content 
            // length, because otherwise he won't know when he's seen it all! 

            Console.WriteLine("get post data start");
            int content_len = 0;
            MemoryStream ms = new MemoryStream();
            if (this.httpHeaders.ContainsKey("Content-Length"))
            {
                content_len = Convert.ToInt32(this.httpHeaders["Content-Length"]);
                if (content_len > MAX_POST_SIZE)
                {
                    throw new Exception(
                        String.Format("POST Content-Length({0}) too big for this simple server",
                          content_len));
                }
                byte[] buf = new byte[BUF_SIZE];
                int to_read = content_len;
                while (to_read > 0)
                {
                    Console.WriteLine("starting Read, to_read={0}", to_read);

                    int numread = this.inputStream.Read(buf, 0, Math.Min(BUF_SIZE, to_read));
                    Console.WriteLine("read finished, numread={0}", numread);
                    if (numread == 0)
                    {
                        if (to_read == 0)
                        {
                            break;
                        }
                        else
                        {
                            throw new Exception("client disconnected during post");
                        }
                    }
                    to_read -= numread;
                    ms.Write(buf, 0, numread);
                }
                ms.Seek(0, SeekOrigin.Begin);
            }
            Console.WriteLine("get post data end");
            srv.handlePOSTRequest(this, new StreamReader(ms));

        }

        public void writeSuccess()
        {
            outputStream.WriteLine("HTTP/1.0 200 OK");
            outputStream.WriteLine("Content-Type: text/html");
            outputStream.WriteLine("Connection: close");
            outputStream.WriteLine("");
        }

        public void writeFailure()
        {
            outputStream.WriteLine("HTTP/1.0 404 File not found");
            outputStream.WriteLine("Connection: close");
            outputStream.WriteLine("");
        }
    }

    public abstract class HttpServer
    {

        protected int port;
        TcpListener listener;
        bool is_active = true;

        public HttpServer(int port)
        {
            this.port = port;
        }

        public void listen()
        {
            listener = new TcpListener(port);
            listener.Start();
            while (is_active)
            {
                TcpClient s = listener.AcceptTcpClient();
                HttpProcessor processor = new HttpProcessor(s, this);
                Thread thread = new Thread(new ThreadStart(processor.process));
                thread.Start();
                Thread.Sleep(1);
            }
        }

        public abstract void handleGETRequest(HttpProcessor p);
        public abstract void handlePOSTRequest(HttpProcessor p, StreamReader inputData);
    }

    public class MyHttpServer : HttpServer
    {
        public MyHttpServer(int port)
            : base(port)
        {
        }
        public override void handleGETRequest(HttpProcessor p)
        {
            Console.WriteLine("request: {0}", p.http_url);
            p.writeSuccess();
            p.outputStream.WriteLine("<html><body><h1>test server</h1>");
            p.outputStream.WriteLine("Current Time: " + DateTime.Now.ToString());
            p.outputStream.WriteLine("url : {0}", p.http_url);

            p.outputStream.WriteLine("<form method=post action=/form>");
            p.outputStream.WriteLine("<input type=text name=foo value=foovalue>");
            p.outputStream.WriteLine("<input type=submit name=bar value=barvalue>");
            p.outputStream.WriteLine("</form>");
        }

        private long currentUnixTime()
        {
            TimeSpan _TimeSpan = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0));
            return (long)_TimeSpan.TotalSeconds;
        }

        private string GetOCRText(string base64img)
        {
            byte[] imageBytes = Convert.FromBase64String(base64img);
            MemoryStream stream = new MemoryStream(imageBytes, 0, imageBytes.Length);
            stream.Write(imageBytes, 0, imageBytes.Length);
            Image bitmap = Image.FromStream(stream, true);

            Console.WriteLine(bitmap.Width);
            Console.WriteLine(bitmap.Height);

            string pageToBeChange = "ocr";

            string strNamespace = "http://schemas.microsoft.com/office/onenote/2010/onenote";
            OneNote.Application onApplication = new OneNote.Application();
            string notebookXml;
            onApplication.GetHierarchy(null, HierarchyScope.hsPages, out notebookXml);
            XDocument doc = XDocument.Parse(notebookXml);
            XNamespace ns = doc.Root.Name.Namespace;
            XElement pageNode = doc.Descendants(ns + "Page").FirstOrDefault();
            string existingPageId = pageNode.Attribute("ID").Value;
            string origPageXML;
            onApplication.GetPageContent(existingPageId, out origPageXML, OneNote.PageInfo.piAll);
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(origPageXML);
            XmlNamespaceManager nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
            nsmgr.AddNamespace("one", strNamespace);
            XmlNodeList imageNodes = xmlDoc.SelectNodes("//one:Image", nsmgr);
            for (int i = 0; i < imageNodes.Count; i++)
            {
                if (imageNodes[i].Attributes["objectID"] != null)
                {
                    onApplication.DeletePageContent(existingPageId, imageNodes[i].Attributes["objectID"].Value);
                }
            }


            string m_xmlImageContent =
"<one:Image><one:Size width=\"{1}\" height=\"{2}\" isSetByUser=\"true\" /><one:Data>{0}</one:Data>" +
                //"<one:OCRData lang=\"zh-TW\">" +
                //"<one:OCRToken startPos=\"0\" region=\"0\" line=\"0\" x=\"5.27999973297119\" y=\"4.800000190734863\" width=\"48.72000122070312\" height=\"51.8400001525879\"/>" +
                //"</one:OCRData>" +
"</one:Image>";
            string m_xmlNewOutline =
               "<?xml version=\"1.0\"?><one:Page xmlns:one=\"{2}\" ID=\"{1}\"><one:Title><one:OE><one:T><![CDATA[{3}]]></one:T></one:OE></one:Title>{0}</one:Page>";

            if (pageNode != null)
            {
                string imageXmlStr = string.Format(m_xmlImageContent, base64img, bitmap.Width, bitmap.Height);
                string pageChangesXml = string.Format(m_xmlNewOutline, new object[] { imageXmlStr, existingPageId, strNamespace, pageToBeChange });
                onApplication.UpdatePageContent(pageChangesXml.ToString(), DateTime.MinValue);
                //onenoteApp.UpdateHierarchy(pageChangesXml);
            }

            long startTime = currentUnixTime();
            string strPageContent = "";
            while (strPageContent == "")
            {
                string strHierarchy;
                string strXML;
                var onApp = onApplication;
                // Get the hierarchy from the root to pages
                onApp.GetHierarchy(System.String.Empty, OneNote.HierarchyScope.hsPages, out strHierarchy);

                // Load the xml into a document
                xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(strHierarchy);

                //Create an XmlNamespaceManager for resolving namespaces.
                nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
                nsmgr.AddNamespace("one", strNamespace);

                // Find the page ID of the active page
                XmlElement xmlActivePage = (XmlElement)xmlDoc.SelectSingleNode("//one:Page", nsmgr);
                string strActivePageID = xmlActivePage.GetAttribute("ID");

                // Get the content from the active page
                onApp.GetPageContent(strActivePageID, out strXML, OneNote.PageInfo.piBinaryData);
                xmlDoc.LoadXml(strXML);

                //Get the data in the T nodes
                string strOcrContent = "";
                strPageContent = "";
                XmlNodeList elemList = xmlDoc.GetElementsByTagName("one:OCRText");
                for (int i = 0; i < elemList.Count; i++)
                {
                    strOcrContent = elemList[i].InnerText; //Get the contents of the <![CDATA[]] block
                    strPageContent = strPageContent + strOcrContent;
                }
                if (currentUnixTime() > startTime + 3) break;
            }
            if (strPageContent == "")
                return "<No OCR content on this page>";
            else
                return strPageContent;

        }

        public override void handlePOSTRequest(HttpProcessor p, StreamReader inputData)
        {
            Console.WriteLine("POST request: {0}", p.http_url);
            //string fileString = Convert.ToBase64String(inputData.ToArray());
            string base64img = inputData.ReadToEnd();
            string ocrtext = GetOCRText(base64img);
            p.outputStream.WriteLine(ocrtext);
        }
    }

    public class TestMain
    {
        private static string getIPAddress()
        {
            IPHostEntry host;
            string localIP = "?";
            host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (IPAddress ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    localIP = ip.ToString();
                }
            }
            return localIP;
        }

        public static int Main(String[] args)
        {
            string ipaddress = getIPAddress();
            string url = "http://transgame.csail.mit.edu:9537/?varname=win7ipaddress&set=" + ipaddress;
            WebClient webClient = new WebClient();
            webClient.DownloadString(url);
            Console.WriteLine("done uploading ip");
            HttpServer httpServer;
            if (args.GetLength(0) > 0)
            {
                httpServer = new MyHttpServer(Convert.ToInt16(args[0]));
            }
            else
            {
                httpServer = new MyHttpServer(8080);
            }
            Thread thread = new Thread(new ThreadStart(httpServer.listen));
            thread.Start();
            Console.WriteLine("server started");
            return 0;
        }

    }

}



