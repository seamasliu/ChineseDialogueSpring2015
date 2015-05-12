﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Dialogue_Data_Entry;
using AIMLbot;

namespace Dialogue_Data_Entry
{
    enum Direction : int
    {
        NORTH = 1, SOUTH = -1,
        EAST = 2, WEST = -2,
        NORTHEAST = 3, SOUTHWEST = -3,
        NORTHWEST = 4, SOUTHEAST = -4,
        CONTAIN = 5, INSIDE = -5,
        HOSTED = 6, WAS_HOSTED_AT = -6,
        WON = 0
    }
    enum Question : int
    {
        WHAT = 0, WHERE = 1, WHEN = 2
    }

    /// <summary>
    /// A data structure to hold information about a query
    /// </summary>
    class Query
    {
        // The name of the Feature that the user asked about
        public Feature MainTopic { get; private set; }
        // Whether or not the input was an explicit question
        public bool IsQuestion { get { return QuestionType != null; } }
        // The type of Question
        public Question? QuestionType { get; private set; }
        // The direction/relationship asked about.
        public Direction? Direction { get; private set; }
        public bool HasDirection { get { return Direction != null; } }

        public Query(Feature mainTopic, Question? questionType, Direction? directions)
        {
            MainTopic = mainTopic;
            QuestionType = questionType;
            Direction = directions;
        }
        public override string ToString()
        {
            string s = "Topic: " + MainTopic.Data;
            s += "\nQuestion type: " + QuestionType ?? "none";
            s += "\nDirection specified: " + Direction ?? "none";
            return s;
        }
    }

    /// <summary>
    /// A utility class to parse natural input into a Query and a Query into natural output.
    /// </summary>
    class QueryHandler
    {
        private const string FORMAT = "FORMAT:";
        private const string IDK = "I'm afraid I don't know anything about that topic.";
        private string[] punctuation = { ",", ";", ".", "?", "!", "\'", "\"", "(", ")", "-" };
        private string[] questionWords = { "?", "what", "where", "when" };
        private string[] directionWords = {"inside", "contain", "north", "east", "west", "south",
                                      "northeast", "northwest", "southeast", "southwest",
                                      "hosted", "was_hosted_at", "won"};
        // "is in" -> contains?
        private Bot bot;
        private User user;
        private FeatureGraph graph;
        private Feature topic;
        private List<string> features;
        private string[] _buffer;
        private string[] buffer { get { return _buffer; } set { _buffer = value; b = 0; } }
        private int b;  // buffer index gets reset when buffer does
        private int turn;
        private int noveltyAmount = 5;

        /// <summary>
        /// Create a converter for the specified XML file
        /// </summary>
        /// <param name="xmlFilename"></param>
        public QueryHandler(FeatureGraph graph)
        {
            // Load the AIML Bot
            this.bot = new Bot();
            bot.loadSettings();
            bot.isAcceptingUserInput = false;
            bot.loadAIMLFromFiles();
            bot.isAcceptingUserInput = true;
            this.user = new User("user", this.bot);

            // Load the Feature Graph
            this.graph = graph;

            // Feature Names, with which to index the graph
            this.features = graph.getFeatureNames();

            this.turn = 1;
            this.topic = null;
        }

        private string MessageToServer(Feature feat, string speak, string noveltyInfo)
        {
            return "ID:" + this.graph.getFeatureIndex(feat.Data) + ":Speak:" + speak + ":" + noveltyInfo;
        }

        public string ParseInput(string input, bool messageToServer = false)
        {
            string answer = IDK;
            string noveltyInfo = "";
            double currentTopicNovelty = -1;
            // Pre-processing
            // Lowercase for comparisons
            input = input.Trim().ToLower();

            if (!string.IsNullOrEmpty(input))
            {
                // Check to see if the AIML Bot has anything to say
                Request request = new Request(input, this.user, this.bot);
                Result result = bot.Chat(request);
                string output = result.Output;

                if (output.Length > 0)
                {
                    if (!output.StartsWith(FORMAT))
                        return output;

                    // MessageBox.Show("Converted output reads: " + output);
                    input = output.Replace(FORMAT, "").ToLower();
                }
            }

            // Spread out punctuation
            input = PadPunctuation(input);

            // Check
            if (this.topic == null)
                this.topic = this.graph.Root;
            FeatureSpeaker speaker = new FeatureSpeaker();
            // CASE: Nothing / Move on to next topic
            if (string.IsNullOrEmpty(input))
            {
                Feature nextTopic = this.topic;
                string[] newBuffer;

                // Can't guarantee it'll actually move on to anything...
                nextTopic = speaker.getNextTopic(this.graph, nextTopic, "", this.turn);
                noveltyInfo = speaker.getNovelty(this.graph, nextTopic, this.turn, noveltyAmount);
                currentTopicNovelty = speaker.getCurrentTopicNovelty();
                newBuffer = FindStuffToSay(nextTopic);
                //MessageBox.Show("Explored " + nextTopic.Data + " with " + newBuffer.Length + " speaks.");

                nextTopic.DiscussedAmount += 1;
                this.graph.setFeatureDiscussedAmount(nextTopic.Data, nextTopic.DiscussedAmount);
                this.topic = nextTopic;
                // talk about
                this.buffer = newBuffer;
                answer = this.buffer[b++];
            }
            // CASE: Tell me more / Continue speaking
            else if (input.Contains("more") && input.Contains("tell"))
            {
                this.topic.DiscussedAmount += 1;
                this.graph.setFeatureDiscussedAmount(this.topic.Data, this.topic.DiscussedAmount);
                // talk about
                if (b < this.buffer.Length)
                    answer = this.buffer[b++];
                else
                    answer = "I've said all I can about that topic!";
                noveltyInfo = speaker.getNovelty(this.graph, this.topic, this.turn, noveltyAmount);
            }
            // CASE: New topic/question
            else
            {
                Query query = BuildQuery(input);
                if (query == null)
                {
                    answer = "I'm sorry, but I don't understand what you are asking.";
                }
                else
                {
                    Feature feature = query.MainTopic;
                    feature.DiscussedAmount += 1;
                    this.graph.setFeatureDiscussedAmount(feature.Data, feature.DiscussedAmount);
                    this.topic = feature;
                    this.buffer = ParseQuery(query);
                    answer = this.buffer[b++];
                    noveltyInfo = speaker.getNovelty(this.graph, this.topic, this.turn, noveltyAmount);
                }
            }

            this.turn++;

            if (answer.Length == 0)
            {
                return IDK;
            }
            else
            {
                if (messageToServer)
                {
                    return MessageToServer(this.topic, answer, noveltyInfo);
                }
                return answer + " " + noveltyInfo;
            }
        }

        /// <summary>
        /// Convert a regular string to a Query object,
        /// identifying the MainTopic and any question and direction words
        /// </summary>
        /// <param name="input">A string of input, asking about a topic</param>
        /// <returns>A Query object that can be passed to ParseQuery for output.</returns>
        public Query BuildQuery(string input)
        {
            string mainTopic;
            Question? questionType = null;
            Direction? directionType = null;

            // Find the main topic!
            Feature f = FindFeature(input);
            if (f == null)
            {
                //MessageBox.Show("FindFeature returned null for input: " + input);
                return null;
            }
            this.topic = f;
            mainTopic = this.topic.Data;
            if (string.IsNullOrEmpty(mainTopic))
            {
                //MessageBox.Show("mainTopic IsNullOrEmpty");
                return null;
            }

            // Is the input a question?
            if (input.Contains("where"))
            {
                questionType = Question.WHERE;
                if (input.Contains("was_hosted_at"))
                {
                    directionType = Direction.WAS_HOSTED_AT;
                }
            }
            else if (input.Contains("when"))
            {
                questionType = Question.WHEN;
            }
            else if (input.Contains("what") || input.Contains("?"))
            {
                questionType = Question.WHAT;
                // Check for direction words
                foreach (string direction in directionWords)
                {
                    // Ideally only one direction is specified
                    if (input.Contains(direction))
                    {
                        directionType = (Direction)Enum.Parse(typeof(Direction), direction, true);
                        // Don't break. If "northwest" is asked, "north" will match first
                        // but then get replaced by "northwest" (and so on).
                    }
                }
            }
            else
            {
                int t = input.IndexOf("tell"), m = input.IndexOf("me"), a = input.IndexOf("about");
                if (0 <= t && t < m && m < a)
                {
                    // "Tell me about" in that order, with any words or so in between
                    // TODO:  Anything?  Should just talk about the topic, then.
                }
            }
            return new Query(this.graph.getFeature(mainTopic), questionType, directionType);
        }

        private string PadPunctuation(string s)
        {
            foreach (string p in punctuation)
            {
                s = s.Replace(p, " " + p);
            }
            return s;
        }
        private string RemovePunctuation(string s)
        {
            foreach (string p in punctuation)
            {
                s = s.Replace(p, "");
            }
            string[] s0 = s.Split(' ');
            return string.Join(" ", s0);
        }

        private Feature FindFeature(string input)
        {
            Feature target = null;
            int targetLen = 0;
            input = input.ToLower();
            foreach (string item in this.features)
            {
                if (input.Contains(RemovePunctuation(item.ToLower())))
                {
                    if (item.Length > targetLen)
                    {
                        target = this.graph.getFeature(item);
                        targetLen = target.Data.Length;
                    }
                }
            }
            return target;
        }

        /// <summary>
        /// Takes a Query object and builds a list of output strings
        /// to talk about the query's MainTopic with its specified question
        /// words and direction words, if any, into consideration.
        /// </summary>
        /// <param name="query"></param>
        /// <returns>List of output strings.</returns>
        public string[] ParseQuery(Query query)
        {
            if (query == null)
                return new string[] { "I don't know." };

            List<string> output = new List<string>();

            if (query.IsQuestion)
            {
                switch (query.QuestionType)
                {
                    case Question.WHAT:
                        if (query.HasDirection)
                        {
                            // e.g. What is Direction of Topic?
                            // Find names of features that is DIRECTION of MainTopic
                            // Get list of <neighbor> tags
                            string dir = query.Direction.ToString().ToLower();
                            if (query.Direction == Direction.WON)
                            {
                                string[] neighbors = FindNeighborsByRelationship(query.MainTopic, dir);
                                // If the topic has no "won" links, then it is the event
                                if (neighbors.Length == 0)
                                {
                                    // So find the winner among its available neighbors
                                    neighbors = FindNeighborsByRelationship(query.MainTopic, "");
                                    foreach (string neighbor in neighbors)
                                    {
                                        // Look at ITS neighbors and see if there is a "won" whose name matches this one
                                        Feature nf = this.graph.getFeature(neighbor);
                                        foreach (var triple in nf.Neighbors)
                                        {
                                            if (triple.Item1.Data == query.MainTopic.Data && triple.Item3 == "won")
                                                output.Add(string.Format("{0} won {1}.", neighbor, query.MainTopic.Data));
                                        }
                                    }
                                }
                                // Otherwise it is the winner
                                else
                                {
                                    output.Add(string.Format("{0} won {1}.", query.MainTopic.Data, neighbors.ToList().JoinAnd()));
                                }
                            }
                            else if (query.Direction == Direction.HOSTED)
                            {
                                string[] neighbors = FindNeighborsByRelationship(query.MainTopic, dir);
                                if (neighbors.Length > 0)
                                    output.Add(string.Format("{0} hosted {1}.", query.MainTopic.Data, neighbors.ToList().JoinAnd()));
                            }
                            else
                            {
                                string[] neighbors = FindNeighborsByRelationship(query.MainTopic, dir);
                                if (neighbors.Length > 0)
                                    output.Add(string.Format("{0} of {1} {2} {3}", dir.ToUpperFirst(), query.MainTopic.Data,
                                        (neighbors.Length > 1) ? "are" : "is", neighbors.ToList().JoinAnd()));
                            }
                        }
                        else
                        {
                            // e.g. What is Topic?
                            // Get the <speak> attribute, if able
                            string[] speak = FindStuffToSay(query.MainTopic);
                            if (speak.Length > 0)
                                output.AddRange(speak);
                        }
                        break;
                    case Question.WHERE:
                        // e.g. "Where was Topic hosted at?"
                        if (query.HasDirection && query.Direction == Direction.WAS_HOSTED_AT)
                        {
                            string[] hostedAt = FindNeighborsByRelationship(query.MainTopic, query.Direction.ToString());
                            // Should only have one host, but treat it as an array
                            foreach (string host in hostedAt)
                                output.Add(query.MainTopic + " was hosted at " + host + ".");
                        }
                        else
                        {
                            // e.g. Where is Topic?
                            // Get all the neighbors from this feature and the "opposite" directions
                            output.AddRange((SpeakNeighborRelations(query.MainTopic.Data, FindAllNeighbors(query.MainTopic))));
                        }
                        break;
                    case Question.WHEN:
                        // e.g. When was Topic made/built/etc.?
                        break;
                }
            }
            else
            {
                // e.g.:
                // Tell me about Topic.
                // Topic.
                output.AddRange(FindStuffToSay(query.MainTopic));
            }

            return output.Count() > 0 ? output.ToArray() : new string[] { IDK };
        }

        private string[] FindSpeak(Feature feature)
        {
            return feature.Speaks.ToArray();
        }

        private string[] FindStuffToSay(Feature feature)
        {
            List<string> stuff = new List<string>();
            string[] speaks = FindSpeak(feature);
            if (speaks.Length > 0)
                stuff.AddRange(speaks);
            stuff.AddRange(SpeakNeighborRelations(feature.Data, FindAllNeighbors(feature)));
            if (stuff.Count() == 0)
            {
                stuff.Add(feature.Data);
            }
            return stuff.ToArray();
        }

        private string[] FindNeighborsByRelationship(Feature feature, string relationship)
        {
            List<string> neighborNames = new List<string>();
            var neighbors = feature.Neighbors;
            for (int i = 0; i < neighbors.Count; i++)
            {
                var triple = neighbors[i];
                Feature neighbor = triple.Item1;
                string relation = triple.Item3;
                if (relation.ToLower().Replace(' ', '_') == relationship.ToLower())
                    neighborNames.Add(neighbor.Data);
            }
            return neighborNames.ToArray();
        }

        private Tuple<string, Direction>[] FindAllNeighbors(Feature feature)
        {
            var _neighbors = feature.Neighbors;
            var neighbors = new List<Tuple<string, Direction>>();
            foreach (var triple in _neighbors)
            {
                string neighborName = triple.Item1.Data;
                string relationship = triple.Item3;
                if (directionWords.Contains(relationship))
                    neighbors.Add(new Tuple<string, Direction>(neighborName,
                        ((Direction)Enum.Parse(typeof(Direction), relationship.ToUpper().Replace(' ', '_')))));
            }
            return neighbors.ToArray();
        }

        private string[] SpeakNeighborRelations(string featureName, Tuple<string, Direction>[] neighbors)
        {
            string[] neighborRelations = new string[neighbors.Length];
            if (neighborRelations.Length == 0)
                return new string[] { };
            for (int i = 0; i < neighborRelations.Length; i++)
                neighborRelations[i] = string.Format("{0} is {1} of {2}.",
                    (i == 0) ? featureName : "It",
                    neighbors[i].Item2.Invert().ToString().ToLower(),
                    neighbors[i].Item1);
            return neighborRelations;
        }
    }

    static class ExtensionMethods
    {
        public static Direction Invert(this Direction d)
        {
            return (Direction)(-(int)d);
        }

        public static string ToUpperFirst(this string s)
        {
            return s.Substring(0, 1).ToUpper() + s.Substring(1);
        }

        public static string JoinAnd(this List<string> items)
        {
            switch (items.Count())
            {
                case 0:
                    return "";
                case 1:
                    return items.ElementAt(0);
                case 2:
                    return items.ElementAt(0) + " and " + items.ElementAt(1);
                default:
                    return string.Join(", ", items.GetRange(0, items.Count - 1))
                        + ", and " + items[items.Count - 1];
            }
        }
    }
}