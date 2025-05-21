import zmq
import time
import asyncio
import cv2
import numpy as np
import pyaudio
import ffmpeg
import logging

async def main():
    logging.basicConfig(level=logging.INFO)
    logger = logging.getLogger(__name__)

    context = zmq.Context()
    subscriber = context.socket(zmq.SUB)
    subscriber.connect("tcp://localhost:5555")
    topics = ["SET_JOINT_FORCES", "GET_DOF_START_INDICES", "GET_JOINT_POSITIONS",
              "GET_JOINT_VELOCITIES", "GET_JOINT_FORCES", "GET_JOINT_EXTERNAL_FORCES", "ERROR"]
    for topic in topics:
        subscriber.subscribe(topic)
    logger.info("Subscriber connected to tcp://localhost:5555")

    publisher = context.socket(zmq.PUB)
    publisher.connect("tcp://localhost:5556")
    logger.info("Publisher connected to tcp://localhost:5556")

    time.sleep(1)

    base_port = 8888
    audio_port = 8887
    max_cameras = 10
    stream_processes = []
    stream_dimensions = []
    audio_output = None
    pyaudio_instance = None

    try:
        # Capture audio stream
        audio_url = f"udp://127.0.0.1:{audio_port}"
        try:
            probe = ffmpeg.probe(audio_url, timeout=2)
            audio_stream = next((s for s in probe['streams'] if s['codec_type'] == 'audio'), None)
            if audio_stream:
                channels = int(audio_stream['channels'])
                sample_rate = int(audio_stream['sample_rate'])
                pyaudio_instance = pyaudio.PyAudio()
                audio_output = pyaudio_instance.open(
                    format=pyaudio.paInt16,
                    channels=channels,
                    rate=sample_rate,
                    output=True
                )
                audio_process = (
                    ffmpeg
                    .input(audio_url, f='webm')
                    .output('pipe:', format='s16le', ac=channels, ar=sample_rate)
                    .run_async(pipe_stdout=True, pipe_stderr=True)
                )
                stream_processes.append(audio_process)
                logger.info(f"Started capturing audio at {audio_url} with {channels} channels")
        except ffmpeg.Error as e:
            logger.warning(f"No audio stream at {audio_url}: {e.stderr.decode()}")

        # Capture video streams
        for port in range(base_port, base_port + max_cameras):
            stream_url = f"udp://127.0.0.1:{port}"
            try:
                probe = ffmpeg.probe(stream_url, timeout=2)
                video_stream = next((s for s in probe['streams'] if s['codec_type'] == 'video'), None)
                if not video_stream:
                    logger.info(f"No video stream at {stream_url}")
                    continue

                width = int(video_stream['width'])
                height = int(video_stream['height'])
                stream_dimensions.append((width, height, port))

                process = (
                    ffmpeg
                    .input(stream_url, f='webm')
                    .output('pipe:', format='rawvideo', pix_fmt='bgr24')
                    .run_async(pipe_stdout=True, pipe_stderr=True)
                )
                stream_processes.append(process)
                logger.info(f"Started capturing video at {stream_url}")
            except ffmpeg.Error as e:
                logger.info(f"No stream at {stream_url}: {e.stderr.decode()}")
                break

        if not stream_processes and not audio_output:
            logger.error("No streams detected. Exiting.")
            return

        try:
            while True:
                # Read video frames
                for i, (process, (width, height, port)) in enumerate(zip(stream_processes, stream_dimensions)):
                    if 'rawvideo' in process.args:  # Video process
                        in_bytes = process.stdout.read(width * height * 3)
                        if not in_bytes:
                            logger.warning(f"End of video stream at port {port}")
                            break
                        frame = np.frombuffer(in_bytes, np.uint8).reshape(height, width, 3)
                        cv2.imshow(f"Unity Stream {port}", frame)

                # Read audio
                if audio_output:
                    audio_bytes = stream_processes[0].stdout.read(4096)
                    if audio_bytes:
                        audio_output.write(audio_bytes)

                # Handle NetMQ
                try:
                    topic = subscriber.recv_string(zmq.NOBLOCK)
                    message = subscriber.recv_string()
                    logger.info(f"Received: {topic}:{message}")
                except zmq.Again:
                    pass

                print("\nAvailable commands:")
                print("1. Set joint forces (e.g., 100,200,300)")
                print("2. Get DOF start indices")
                print("3. Get joint positions")
                print("4. Get joint velocities")
                print("5. Get joint forces")
                print("6. Get joint external forces")
                print("7. Exit")
                choice = input("Enter choice (1-7): ")

                command = None
                if choice == "1":
                    forces = input("Enter forces (comma-separated, e.g., 100,200,300): ")
                    command = f"SET_JOINT_FORCES:{forces}"
                elif choice == "2":
                    command = "GET_DOF_START_INDICES:"
                elif choice == "3":
                    command = "GET_JOINT_POSITIONS:"
                elif choice == "4":
                    command = "GET_JOINT_VELOCITIES:"
                elif choice == "5":
                    command = "GET_JOINT_FORCES:"
                elif choice == "6":
                    command = "GET_JOINT_EXTERNAL_FORCES:"
                elif choice == "7":
                    print("Exiting...")
                    break
                else:
                    print("Invalid choice, please try again.")
                    continue

                if command:
                    publisher.send_string(command)
                    logger.info(f"Sent: {command}")

                if cv2.waitKey(1) & 0xFF == ord('q'):
                    break

        finally:
            for process in stream_processes:
                process.terminate()
            if audio_output:
                audio_output.stop_stream()
                audio_output.close()
            if pyaudio_instance:
                pyaudio_instance.terminate()
            cv2.destroyAllWindows()

    except Exception as e:
        logger.error(f"Error capturing streams: {e}")

    finally:
        subscriber.close()
        publisher.close()
        context.term()

if __name__ == "__main__":
    asyncio.run(main())